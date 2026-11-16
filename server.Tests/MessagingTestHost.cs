using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace paprika.Tests;

internal static class MessagingTestHost
{
    public static IHost Create(
        Store store,
        Providers providers,
        JobCoordinator? jobs = null,
        bool polling = true
    ) =>
        new HostBuilder()
            .ConfigureAppConfiguration(c =>
                c.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["paprika:DataDirectory"] = store.Root }
                )
            )
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            })
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UsePacketMessaging()
            .ConfigureServices(services =>
            {
                services.AddSingleton(store);
                services.AddSingleton(providers);
                services.AddSingleton(jobs ?? new JobCoordinator());
                services.AddSingleton<DeliveryStore>();
            })
            .Build();
}
