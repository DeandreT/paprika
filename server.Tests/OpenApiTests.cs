using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task SpecificationCoversRoutesPublicContractsAndFileTransfers()
    {
        var data = Path.Combine(
            Path.GetTempPath(),
            "paperwork-openapi-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            await using var factory = new ApiFactory(data);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.StartsWith("3.0.", root.GetProperty("openapi").GetString());
            var paths = root.GetProperty("paths");
            var routeSource = factory.Services.GetRequiredService<EndpointDataSource>();
            var routes = routeSource
                .Endpoints.OfType<RouteEndpoint>()
                .Where(e =>
                    e.RoutePattern.RawText?.StartsWith("/api/") == true
                    && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription
                        != true
                )
                .ToList();
            Assert.True(
                routes.Count >= 94,
                "The original and platform API operations must be present."
            );
            Assert.Equal(
                routes
                    .Select(r => r.RoutePattern.RawText!.Replace(":int", "").TrimEnd('/'))
                    .Distinct()
                    .Order(),
                paths.EnumerateObject().Select(p => p.Name.TrimEnd('/')).Order()
            );
            var names = new HashSet<string>();
            foreach (var route in routes)
            {
                foreach (
                    var method in route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                )
                {
                    var path = paths
                        .EnumerateObject()
                        .Single(p =>
                            p.Name.TrimEnd('/')
                            == route.RoutePattern.RawText!.Replace(":int", "").TrimEnd('/')
                        );
                    var operation = path.Value.GetProperty(method.ToLowerInvariant());
                    Assert.True(names.Add(operation.GetProperty("operationId").GetString()!));
                    Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
                    Assert.True(operation.GetProperty("responses").TryGetProperty("500", out _));
                    if (method is "POST" or "PUT")
                    {
                        var marker = operation
                            .GetProperty("parameters")
                            .EnumerateArray()
                            .Single(p =>
                                p.GetProperty("name").GetString() == "X-PaperworkClassifier"
                            );
                        Assert.True(marker.GetProperty("required").GetBoolean());
                        Assert.Equal(
                            "1",
                            marker.GetProperty("schema").GetProperty("enum")[0].GetString()
                        );
                    }
                }
            }
            var schemas = root.GetProperty("components").GetProperty("schemas");
            var packet = schemas.GetProperty("PacketResponse").GetProperty("properties");
            Assert.True(packet.TryGetProperty("pages", out _));
            Assert.False(packet.TryGetProperty("sources", out _));
            Assert.DoesNotContain("ocrJson", schemas.GetRawText());
            Assert.DoesNotContain("operationUrl", schemas.GetRawText());
            var upload = paths
                .GetProperty("/api/packets")
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("multipart/form-data")
                .GetProperty("schema");
            Assert.Equal("files", upload.GetProperty("required")[0].GetString());
            Assert.Equal(
                "binary",
                upload
                    .GetProperty("properties")
                    .GetProperty("files")
                    .GetProperty("items")
                    .GetProperty("format")
                    .GetString()
            );
            var review = paths.GetProperty("/api/packets/{id}/review").GetProperty("put");
            Assert.Contains("ReviewRequest", review.GetProperty("requestBody").GetRawText());
            Assert.True(review.GetProperty("responses").TryGetProperty("409", out _));
            var export = paths.GetProperty("/api/packets/{id}/export").GetProperty("get");
            Assert.Equal(
                "binary",
                export
                    .GetProperty("responses")
                    .GetProperty("200")
                    .GetProperty("content")
                    .GetProperty("application/zip")
                    .GetProperty("schema")
                    .GetProperty("format")
                    .GetString()
            );

            var history = paths.GetProperty("/api/packets/{id}/history").GetProperty("get");
            Assert.Contains(
                "HistoryResponse",
                history.GetProperty("responses").GetProperty("200").GetRawText()
            );
            var download = paths
                .GetProperty("/api/packets/{id}/documents/{documentId}/download")
                .GetProperty("get");
            Assert.True(download.GetProperty("responses").TryGetProperty("404", out _));
            Assert.True(download.GetProperty("responses").TryGetProperty("409", out _));
            Assert.Equal(
                "binary",
                download
                    .GetProperty("responses")
                    .GetProperty("200")
                    .GetProperty("content")
                    .GetProperty("application/pdf")
                    .GetProperty("schema")
                    .GetProperty("format")
                    .GetString()
            );
            // Runtime JSON must still match the named response shape after replacing anonymous DTOs.
            using var demoRequest = new HttpRequestMessage(HttpMethod.Post, "/api/packets/demo");
            demoRequest.Headers.Add("X-PaperworkClassifier", "1");
            using var demoResponse = await client.SendAsync(demoRequest);
            demoResponse.EnsureSuccessStatusCode();
            using var demo = JsonDocument.Parse(await demoResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                packet.EnumerateObject().Select(p => p.Name).Order(),
                demo.RootElement.EnumerateObject().Select(p => p.Name).Order()
            );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(data))
                Directory.Delete(data, true);
        }
    }

    private sealed class ApiFactory(string data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Paperwork:DataDirectory"] = data,
                            ["Paperwork:Mode"] = "Demo",
                        }
                    )
            );
    }
}
