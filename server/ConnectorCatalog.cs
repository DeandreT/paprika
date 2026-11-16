using System.Net.Http.Headers;
using System.Text.Json;

namespace paprika;

public sealed record ConnectorInfo(
    string Id,
    string Kind,
    bool Configured,
    bool RequiresIdempotentReceiver
);

public sealed class ConnectorCatalog(IConfiguration configuration)
{
    internal IConfigurationSection Configuration(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,80}$"))
            throw new InvalidDataException("Invalid connector ID.");
        var section = configuration.GetSection("paprika:Connectors").GetSection(id);
        if (!section.Exists())
            throw new InvalidDataException("Connector is not configured.");
        if (section["Protocol"] is not null)
            throw new InvalidDataException("Custom connector protocols are not supported.");
        return section;
    }

    public ConnectorInfo[] All() =>
        configuration
            .GetSection("paprika:Connectors")
            .GetChildren()
            .Select(s => new ConnectorInfo(
                s.Key,
                s["Kind"] ?? "",
                true,
                s["Kind"] == "httpDelivery"
            ))
            .ToArray();

    public string BindingHash(string id)
    {
        var c = Configuration(id);
        var binding = new
        {
            id,
            kind = c["Kind"],
            root = c["Root"],
            endpoint = c["Endpoint"],
            idempotency = c["SupportsIdempotency"],
        };
        return ReferencePolicies.Hash(binding);
    }

    internal string Root(string id, string kind)
    {
        var config = Configuration(id);
        if (
            config["Kind"] != kind
            || string.IsNullOrWhiteSpace(config["Root"])
            || !Path.IsPathFullyQualified(config["Root"]!)
        )
            throw new InvalidDataException("Connector requires an approved absolute root.");
        var root = Path.GetFullPath(config["Root"]!);
        if (!Directory.Exists(root))
            throw new InvalidDataException("Configured root is unavailable.");
        var current = new DirectoryInfo(root);
        for (DirectoryInfo? dir = current; dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse roots are not allowed.");
        return root;
    }

    internal (HttpClient Client, HttpRequestMessage Request) Request(
        string id,
        string kind,
        HttpMethod method
    )
    {
        var config = Configuration(id);
        if (
            config["Kind"] != kind
            || !Uri.TryCreate(config["Endpoint"], UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || uri.OriginalString.Length > 2048
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        )
            throw new InvalidDataException(
                "A configured HTTPS endpoint (or loopback test endpoint) is required."
            );
        var client = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var request = new HttpRequestMessage(method, uri);
        // Secrets are read only at execution time and never enter DTOs or the ledger.
        var token = config["BearerToken"];
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, request);
    }

    internal static async Task<byte[]> BoundedAsync(
        HttpContent content,
        int limit,
        CancellationToken ct
    )
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("Response is too large.");
        using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task<ReferenceEntry[]> LookupAsync(
        string id,
        string reference,
        CancellationToken ct
    )
    {
        if (reference.Length > 500)
            throw new InvalidDataException("Reference is too long.");
        var pair = Request(id, "httpLookup", HttpMethod.Post);
        using var client = pair.Client;
        using var request = pair.Request;
        request.Content = JsonContent.Create(new { reference }, options: Contract.Json);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Lookup unavailable.");
        var bytes = await BoundedAsync(response.Content, 256 * 1024, ct);
        var rows = JsonSerializer.Deserialize<ReferenceEntry[]>(bytes, Contract.Json)
            ?? throw new InvalidDataException("Invalid lookup response.");
        if (
            rows.Length > 100
            || rows.Any(e =>
                e is null
                || e.Reference != reference
                || string.IsNullOrWhiteSpace(e.RecordId)
                || e.RecordId.Length > 200
                || e.Label is null
                || e.Label.Length > 500
            )
        )
            throw new InvalidDataException("Invalid lookup response.");
        return rows;
    }
}
