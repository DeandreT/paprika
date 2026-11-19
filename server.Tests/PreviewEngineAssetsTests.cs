using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace paprika.Tests;

public sealed class PreviewEngineAssetsTests : IClassFixture<PreviewEngineAssetsTests.Site>
{
    private const string Prefix = "/assets/preview-engine/test-v1/";
    private static readonly byte[] Wasm = [0, 97, 115, 109, 1, 0, 0, 0];
    private readonly Site site;

    public PreviewEngineAssetsTests(Site site) => this.site = site;

    [Theory]
    [InlineData(null, null)]
    [InlineData("br, gzip", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("br;q=0, gzip;q=0", null)]
    [InlineData("br;q=0.2, gzip;q=0.8", "gzip")]
    [InlineData("identity;q=1, br;q=0.5", null)]
    [InlineData("*;q=1, br;q=0", "gzip")]
    public async Task NegotiatesVersionedWasm(string? accept, string? encoding)
    {
        using var client = site.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Prefix + "engine.wasm");
        if (accept is not null)
            request.Headers.Add("Accept-Encoding", accept);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(encoding, response.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.True(response.Headers.CacheControl?.MaxAge > TimeSpan.Zero);
        Assert.Contains(response.Headers.CacheControl!.Extensions, e => e.Name == "immutable");
        using var body = await response.Content.ReadAsStreamAsync();
        using Stream decoded = encoding switch
        {
            "br" => new BrotliStream(body, CompressionMode.Decompress),
            "gzip" => new GZipStream(body, CompressionMode.Decompress),
            _ => body,
        };
        using var bytes = new MemoryStream();
        await decoded.CopyToAsync(bytes);
        Assert.Equal(Wasm, bytes.ToArray());
    }

    [Theory]
    [InlineData("glue.js", "text/javascript")]
    [InlineData("glue.mjs", "text/javascript")]
    [InlineData("resources/file.bin", "application/octet-stream")]
    [InlineData("resources/file.cff", "application/octet-stream")]
    [InlineData("resources/file.pfb", "application/octet-stream")]
    [InlineData("resources/Example Font (Bold).CFF", "application/octet-stream")]
    public async Task HeadUsesIdentityWhenSidecarsAreAbsent(string name, string mime)
    {
        using var client = site.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, Prefix + name);
        request.Headers.Add("Accept-Encoding", "br, gzip");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(1, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NeverSendsAnUnacceptableRepresentation()
    {
        using var client = site.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "*;q=0");
        using var response = await client.GetAsync(Prefix + "engine.wasm");
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(Prefix + "missing.wasm")]
    [InlineData(Prefix + "missing")]
    [InlineData(Prefix + "engine.wasm.gz")]
    [InlineData(Prefix + "extra.html")]
    [InlineData(Prefix + "../unversioned.js")]
    [InlineData(Prefix + "..\\unversioned.js")]
    [InlineData("/assets/preview-engine/unversioned.js")]
    [InlineData("/assets/preview-engine")]
    public async Task ReservedPathReturns404InsteadOfOrdinaryStaticFileOrSpa(string path)
    {
        // Send the path as received by middleware, without HttpClient URI canonicalization.
        var result = await site.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Host = new HostString("localhost");
            ctx.Request.Path = path;
        });
        Assert.Equal(404, result.Response.StatusCode);
    }

    [Fact]
    public async Task OrdinaryStaticAndSpaFallbackStillWork()
    {
        using var client = site.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip");
        using var regular = await client.GetAsync("/assets/ordinary.js");
        Assert.Equal("ordinary", await regular.Content.ReadAsStringAsync());
        Assert.Empty(regular.Content.Headers.ContentEncoding);
        Assert.Equal("spa", await client.GetStringAsync("/review/page"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/missing")).StatusCode);
    }

    public sealed class Site : WebApplicationFactory<Program>
    {
        private readonly string root = Directory
            .CreateTempSubdirectory("paprika-preview-")
            .FullName;
        private readonly PhysicalFileProvider provider;

        public Site()
        {
            var web = Path.Combine(root, "wwwroot");
            var assets = Directory.CreateDirectory(Path.Combine(web, "assets")).FullName;
            var preview = Directory
                .CreateDirectory(Path.Combine(assets, "preview-engine"))
                .FullName;
            var version = Path.Combine(preview, "test-v1");
            var resources = Directory.CreateDirectory(Path.Combine(version, "resources")).FullName;
            File.WriteAllText(Path.Combine(web, "index.html"), "spa");
            File.WriteAllText(Path.Combine(assets, "ordinary.js"), "ordinary");
            File.WriteAllText(Path.Combine(preview, "unversioned.js"), "outside the version");
            foreach (var name in new[] { "glue.js", "glue.mjs", "extra.html" })
                File.WriteAllText(Path.Combine(version, name), "x");
            foreach (
                var name in new[] { "file.bin", "file.cff", "file.pfb", "Example Font (Bold).CFF" }
            )
                File.WriteAllText(Path.Combine(resources, name), "x");
            var file = Path.Combine(version, "engine.wasm");
            File.WriteAllBytes(file, Wasm);
            using (var output = File.Create(file + ".gz"))
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                gzip.Write(Wasm);
            using (var output = File.Create(file + ".br"))
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
                brotli.Write(Wasm);
            provider = new PhysicalFileProvider(web);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = Path.Combine(root, "data"),
                        }
                    )
            );
            builder.ConfigureServices(
                (context, _) =>
                {
                    context.HostingEnvironment.WebRootPath = provider.Root;
                    context.HostingEnvironment.WebRootFileProvider = provider;
                }
            );
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
