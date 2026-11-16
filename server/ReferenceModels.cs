using System.Security.Cryptography;
using System.Text.Json;

namespace paprika;

public sealed record ReferencePolicy(
    string Mode,
    string? DatasetId = null,
    int? DatasetVersion = null,
    string? DatasetHash = null,
    string? ConnectorId = null,
    bool RequireVerified = false,
    bool AutoVerify = false,
    string[]? IdentifierKinds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
        string? ConnectorBindingHash = null
);

public static class ReferencePolicies
{
    public static void Initialize(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction? tx = null
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS reference_datasets(id TEXT NOT NULL,version INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(id,version)); CREATE TABLE IF NOT EXISTS reference_results(packet_id TEXT NOT NULL,revision INTEGER NOT NULL,document_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,revision,document_id)); CREATE TABLE IF NOT EXISTS reference_connector_bindings(id TEXT PRIMARY KEY,hash TEXT NOT NULL);";
        q.ExecuteNonQuery();
    }

    public static string? Validate(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        ReferencePolicy? policy
    )
    {
        var error = Validate(policy);
        if (error is not null || policy is null)
            return error;
        Initialize(c, tx);
        if (policy.Mode == "http")
        {
            var binding = ConnectorBinding(c, tx, policy.ConnectorId!);
            return
                binding is not null
                && (policy.ConnectorBindingHash is null || policy.ConnectorBindingHash == binding)
                ? null
                : "HTTP reference connector is missing, unapproved or its pinned destination changed.";
        }
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM reference_datasets WHERE id=$id AND version=$version";
        q.Parameters.AddWithValue("$id", policy.DatasetId!);
        q.Parameters.AddWithValue("$version", policy.DatasetVersion!.Value);
        if (q.ExecuteScalar() is string json)
        {
            try
            {
                var data = JsonSerializer.Deserialize<ReferenceDataset>(json, Contract.Json);
                if (
                    data is not null
                    && data.Hash == policy.DatasetHash
                    && DatasetHash(data) == policy.DatasetHash
                )
                    return null;
            }
            catch (JsonException) { }
        }
        return "Pinned reference dataset is missing or its hash does not match.";
    }

    public static ReferencePolicy? Freeze(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        ReferencePolicy? policy
    )
    {
        if (Validate(c, tx, policy) is string error)
            throw new WorkflowException(400, error);
        var frozen = Freeze(policy);
        return frozen?.Mode == "http"
            ? frozen with
            {
                ConnectorBindingHash = ConnectorBinding(c, tx, frozen.ConnectorId!),
            }
            : frozen;
    }

    private static string? ConnectorBinding(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string id
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT hash FROM reference_connector_bindings WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() as string;
    }

    public static bool IsCurrentBinding(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        ReferencePolicy? policy,
        ConnectorCatalog? catalog = null
    )
    {
        if (policy?.Mode != "http")
            return true;
        if (
            policy.ConnectorBindingHash is null
            || ConnectorBinding(c, tx, policy.ConnectorId!) != policy.ConnectorBindingHash
        )
            return false;
        try
        {
            return catalog is null
                || catalog.BindingHash(policy.ConnectorId!) == policy.ConnectorBindingHash;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string? Validate(ReferencePolicy? policy) =>
        policy is null ? null
        : policy.IdentifierKinds is not null
        && (
            policy.IdentifierKinds.Length is 0 or > 16
            || policy.IdentifierKinds.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 80)
            || policy.IdentifierKinds.Distinct(StringComparer.Ordinal).Count()
                != policy.IdentifierKinds.Length
        )
            ? "Reference identifier kinds must be a bounded unique list."
        : policy.Mode switch
        {
            "local"
                when !string.IsNullOrWhiteSpace(policy.DatasetId)
                    && policy.DatasetVersion > 0
                    && policy.DatasetHash is { Length: 64 } => null,
            "http" when !string.IsNullOrWhiteSpace(policy.ConnectorId) => null,
            _ =>
                "Reference policy must pin a local dataset ID/version/hash or a configured HTTP connector.",
        };

    public static ReferencePolicy? Freeze(ReferencePolicy? policy) =>
        policy is null ? null : policy with { IdentifierKinds = policy.IdentifierKinds?.ToArray() };

    public static string Hash<T>(T value) =>
        Convert
            .ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json)))
            .ToLowerInvariant();

    public static string DatasetHash(ReferenceDataset dataset) =>
        Hash(
            new
            {
                dataset.Id,
                version = dataset.Version,
                dataset.Name,
                dataset.Synthetic,
                entries = dataset.Entries,
            }
        );
}

public sealed record ReferenceEntry(string Reference, string RecordId, string Label);

public sealed record ReferenceDatasetRequest(
    string Id,
    string Name,
    bool Synthetic,
    ReferenceEntry[] Entries
);

public sealed record ReferenceDataset(
    string Id,
    int Version,
    string Name,
    bool Synthetic,
    string Hash,
    DateTimeOffset CreatedAt,
    ReferenceEntry[] Entries,
    string? CreatedBy = null
);

public sealed record ReferenceLookupRequest(
    string PacketId,
    int Revision,
    string? DocumentId = null
);

public sealed record ReferenceVerifyRequest(
    string PacketId,
    int Revision,
    string DocumentId,
    string ResultHash,
    string RecordId
);

public sealed record ReferenceResult(
    string PacketId,
    int Revision,
    string DocumentId,
    string Reference,
    string Status,
    ReferenceEntry[] Matches,
    string Provenance,
    string DatasetHash,
    bool Synthetic,
    string ResultHash,
    DateTimeOffset At,
    string WorkflowHash = "",
    string? VerifiedBy = null,
    string[]? CandidateReferences = null,
    string DocumentHash = ""
);
