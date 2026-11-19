using System.Net;

namespace paprika;

public sealed class HostingPolicy
{
    private readonly bool shared;
    private readonly int localPort;
    private readonly HashSet<string> hosts;
    private readonly HashSet<string> origins;
    public string ListenUrl { get; }

    public HostingPolicy(IConfiguration config)
    {
        shared = string.Equals(
            config["paprika:Operations:Mode"],
            "shared",
            StringComparison.OrdinalIgnoreCase
        );
        localPort = shared ? 5080 : config.GetValue("paprika:Hosting:LocalPort", 5080);
        if (localPort is < 1 or > 65535)
            throw new InvalidOperationException("Local port must be between 1 and 65535.");
        ListenUrl = shared
            ? config["paprika:Hosting:Url"] ?? "http://127.0.0.1:5080"
            : $"http://127.0.0.1:{localPort}";
        if (
            !Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen)
            || listen.Scheme is not ("http" or "https")
            || listen.AbsolutePath != "/"
            || listen.UserInfo.Length > 0
            || listen.Query.Length > 0
            || listen.Fragment.Length > 0
        )
            throw new InvalidOperationException(
                "Hosting URL must be an explicit HTTP or HTTPS origin."
            );
        hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "127.0.0.1",
            "[::1]",
            "::1",
        };
        origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (shared)
        {
            foreach (var entry in config.GetSection("paprika:Hosting:AllowedHosts").GetChildren())
            {
                var host = entry.Value;
                if (
                    string.IsNullOrWhiteSpace(host)
                    || host.Contains('*')
                    || host.Contains('/')
                    || host.Any(char.IsWhiteSpace)
                )
                    throw new InvalidOperationException(
                        "Allowed hosts must be explicit host names."
                    );
                hosts.Add(host);
            }
            if (listen.Host is not ("0.0.0.0" or "[::]" or "::"))
                hosts.Add(listen.Host);
            foreach (var entry in config.GetSection("paprika:Hosting:AllowedOrigins").GetChildren())
            {
                if (
                    !Uri.TryCreate(entry.Value, UriKind.Absolute, out var origin)
                    || origin.Scheme is not ("http" or "https")
                    || origin.UserInfo.Length > 0
                    || origin.AbsolutePath != "/"
                    || origin.Query.Length > 0
                    || origin.Fragment.Length > 0
                )
                    throw new InvalidOperationException(
                        "Allowed origins must be explicit HTTP or HTTPS origins."
                    );
                origins.Add(origin.GetLeftPart(UriPartial.Authority));
            }
        }
    }

    public string? Reject(HttpContext context)
    {
        if (!shared && context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
            return "Loopback access only.";
        if (!hosts.Contains(context.Request.Host.Host))
            return "Host is not allowed.";
        var raw = context.Request.Headers.Origin.ToString();
        if (raw.Length == 0)
            return null;
        if (
            !Uri.TryCreate(raw, UriKind.Absolute, out var origin)
            || origin.UserInfo.Length > 0
            || origin.AbsolutePath != "/"
            || origin.Query.Length > 0
            || origin.Fragment.Length > 0
        )
            return "Foreign origins are not allowed.";
        if (!shared)
            return
                origin.Scheme == "http"
                && hosts.Contains(origin.Host)
                && (origin.Port == 4200 || origin.Port == localPort)
                ? null
                : "Foreign origins are not allowed.";
        var authority = origin.GetLeftPart(UriPartial.Authority);
        var sameOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        return
            authority.Equals(sameOrigin, StringComparison.OrdinalIgnoreCase)
            || origins.Contains(authority)
            ? null
            : "Foreign origins are not allowed.";
    }
}
