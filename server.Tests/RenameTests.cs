using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Wolverine.Util;
using Xunit;

namespace paprika.Tests;

public sealed class RenameTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-rename-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void NewInstallationsUseLowercaseDataDirectory() =>
        Assert.Equal(Path.Combine(root, "paprika"), AppPaths.DefaultDataRoot(root));

    [Fact]
    public void ExistingDataIsReusedWithoutMovingOrMerging()
    {
        var legacy = Path.Combine(root, "PaperworkClassifier");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "jobs.sqlite"), "legacy sentinel");
        Directory.CreateDirectory(Path.Combine(root, "paprika"));
        Assert.Equal(legacy, AppPaths.DefaultDataRoot(root));
        Assert.Equal("legacy sentinel", File.ReadAllText(Path.Combine(legacy, "jobs.sqlite")));

        var current = Path.Combine(root, "paprika", "jobs.sqlite");
        File.WriteAllText(current, "current sentinel");
        Assert.Equal(Path.GetDirectoryName(current), AppPaths.DefaultDataRoot(root));
        Assert.Equal("current sentinel", File.ReadAllText(current));
        Assert.True(File.Exists(Path.Combine(legacy, "jobs.sqlite")));
    }

    [Fact]
    public void ExplicitDataDirectoryWinsOverGenericSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = Path.Combine(root, "explicit"),
                    ["DataDirectory"] = Path.Combine(root, "generic"),
                }
            )
            .Build();
        Assert.Equal(Path.Combine(root, "explicit"), AppPaths.DataRoot(configuration));
        configuration["paprika:DataDirectory"] = null;
        Assert.Equal(Path.Combine(root, "generic"), AppPaths.DataRoot(configuration));
    }

    [Theory]
    [InlineData(typeof(ProcessPacket), "PaperworkClassifier.ProcessPacket")]
    [InlineData(typeof(ProcessDelivery), "PaperworkClassifier.ProcessDelivery")]
    [InlineData(typeof(ContinuePlatformFlow), "PaperworkClassifier.ContinuePlatformFlow")]
    public void DurableMessageIdentitiesSurviveNamespaceRename(Type message, string identity) =>
        Assert.Equal(identity, message.ToMessageTypeName());

    [Theory]
    [InlineData("X-paprika", "1", HttpStatusCode.OK)]
    [InlineData("X-PaperworkClassifier", "1", HttpStatusCode.OK)]
    [InlineData("X-paprika", "0", HttpStatusCode.Forbidden)]
    [InlineData("X-unrelated", "1", HttpStatusCode.Forbidden)]
    public async Task RequestMarkerSupportsNewNameAndLegacyClients(
        string header,
        string value,
        HttpStatusCode expected
    )
    {
        using var factory =
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(
                builder =>
                    builder.ConfigureAppConfiguration(
                        (_, configuration) =>
                            configuration.AddInMemoryCollection(
                                new Dictionary<string, string?>
                                {
                                    ["paprika:DataDirectory"] = root,
                                    ["paprika:Mode"] = "simulation",
                                }
                            )
                    )
            );
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/packets/demo");
        request.Headers.Add(header, value);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
