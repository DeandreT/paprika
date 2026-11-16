using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using paprika;
using Xunit;

namespace paprika.Tests;

public class PlatformHostingTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LocalModeRetainsLoopbackBoundary()
    {
        var policy = new HostingPolicy(Config([]));
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 5080);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.15");
        Assert.NotNull(policy.Reject(context));
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.Origin = "https://attacker.example";
        Assert.NotNull(policy.Reject(context));
        context.Request.Headers.Origin = "http://localhost:4200";
        Assert.Null(policy.Reject(context));
    }

    [Fact]
    public void SharedModeRequiresExplicitHostsAndOrigins()
    {
        var policy = new HostingPolicy(
            Config(
                new()
                {
                    ["paprika:Operations:Mode"] = "shared",
                    ["paprika:Hosting:Url"] = "https://0.0.0.0:5443",
                    ["paprika:Hosting:AllowedHosts:0"] = "paprika.example",
                }
            )
        );
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.15");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("paprika.example", 5443);
        context.Request.Headers.Origin = "https://paprika.example:5443";
        Assert.Null(policy.Reject(context));
        context.Request.Headers.Origin = "https://paprika.example.attacker.test:5443";
        Assert.NotNull(policy.Reject(context));
        context.Request.Headers.Origin = "https://paprika.example:5443";
        context.Request.Host = new HostString("attacker.test");
        Assert.NotNull(policy.Reject(context));
    }

    [Fact]
    public void SharedConfigurationRejectsWildcardHosts()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new HostingPolicy(
                Config(
                    new()
                    {
                        ["paprika:Operations:Mode"] = "shared",
                        ["paprika:Hosting:AllowedHosts:0"] = "*",
                    }
                )
            )
        );
    }
}
