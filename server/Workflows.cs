using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed record VersionRef(string Id, int Version);

public sealed record SplitPolicy(string Mode = "inherit", int PageCount = 1, string Pattern = "");

public sealed record TypeConfig(string[] Keywords, string Instructions, SplitPolicy Split);

public sealed record ReleaseConfig(VersionRef[] Types);

public sealed record WorkflowConfig(
    VersionRef Release,
    string Mode,
    string ProviderProfile,
    string Model,
    string Instructions,
    SplitPolicy DefaultSplit,
    string[] IdentifierKinds,
    string[] RequiredIdentifierKinds,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
        WorkflowCapabilities? Capabilities = null
);

public sealed record CatalogEntry(
    string Kind,
    string Id,
    int Version,
    int Revision,
    string Status,
    string Name,
    string Description,
    TypeConfig? DocumentType = null,
    ReleaseConfig? Release = null,
    WorkflowConfig? Workflow = null
);

public sealed record CreateCatalogRequest(
    string Kind,
    string? Id,
    string Name,
    string Description,
    TypeConfig? DocumentType = null,
    ReleaseConfig? Release = null,
    WorkflowConfig? Workflow = null
);

public sealed record WorkflowStats(
    string Id,
    int Version,
    int Packets,
    int Review,
    int Failed,
    int Approved,
    int Corrections
);

public sealed record WorkflowCatalogResponse(
    CatalogEntry[] Entries,
    VersionRef DefaultWorkflow,
    string[] Policies,
    string[] ProviderProfiles,
    WorkflowStats[] Stats,
    string LiveModel = ""
);

public sealed record SnapshotType(
    string Id,
    int Version,
    string Name,
    string Description,
    string[] Keywords,
    string Instructions,
    SplitPolicy Split
);

public sealed record WorkflowSnapshot(
    string Id,
    string Name,
    int Version,
    string ReleaseId,
    int ReleaseVersion,
    string Hash,
    string Mode,
    string ProviderProfile,
    string Model,
    string Instructions,
    SplitPolicy DefaultSplit,
    string[] IdentifierKinds,
    string[] RequiredIdentifierKinds,
    SnapshotType[] Types,
    bool LegacyRules = false,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
        WorkflowCapabilities? Capabilities = null
);

public sealed record WorkflowSelection(
    string? WorkflowId = null,
    int? WorkflowVersion = null,
    bool TestRun = false,
    int? WorkflowRevision = null
);

public sealed class WorkflowException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class WorkflowRegistry(Store store, Settings settings)
{
    private static readonly string[] PolicyModes =
    [
        "inherit",
        "classifier",
        "separate",
        "consecutive",
        "manual",
        "fixed",
        "first",
        "last",
        "identifier",
    ];
    private static readonly string[] IdentifierKinds =
    [
        "load",
        "shipment",
        "order",
        "bol",
        "receipt",
        "mc",
        "dot",
        "trailer",
        "container",
        "chassis",
    ];
    private static readonly Regex IdRule = new(
        "\\A[a-z0-9][a-z0-9_-]{0,79}\\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100)
    );

    private static WorkflowException Invalid(string message) => new(400, message);

    private static WorkflowException Conflict(string message) => new(409, message);

    public void Initialize()
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = Command(
            c,
            tx,
            "CREATE TABLE IF NOT EXISTS workflow_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,version INTEGER NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id,version)); "
                + "CREATE TABLE IF NOT EXISTS workflow_settings(key TEXT PRIMARY KEY,json TEXT NOT NULL);"
        );
        q.ExecuteNonQuery();
        // A marker makes initialization idempotent even after users edit or archive seed entries.
        q.CommandText = "SELECT json FROM workflow_settings WHERE key='initialized'";
        if (q.ExecuteScalar() is null)
        {
            foreach (
                var entry in Seeds(settings.Mode == "live" ? "live" : "simulation", settings.Model)
            )
                Insert(c, tx, entry);
            SaveSetting(c, tx, "default", new VersionRef("general-logistics", 1));
            SaveSetting(c, tx, "initialized", true);
        }
        tx.Commit();
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        return q;
    }

    private static void SaveSetting<T>(
        SqliteConnection c,
        SqliteTransaction tx,
        string key,
        T value
    )
    {
        using var q = Command(
            c,
            tx,
            "INSERT INTO workflow_settings(key,json) VALUES($key,$json) ON CONFLICT(key) DO UPDATE SET json=excluded.json"
        );
        q.Parameters.AddWithValue("$key", key);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Contract.Json));
        q.ExecuteNonQuery();
    }

    private static VersionRef Default(SqliteConnection c, SqliteTransaction tx)
    {
        using var q = Command(c, tx, "SELECT json FROM workflow_settings WHERE key='default'");
        return JsonSerializer.Deserialize<VersionRef>((string)q.ExecuteScalar()!, Contract.Json)!;
    }

    private static CatalogEntry? Find(
        SqliteConnection c,
        SqliteTransaction tx,
        string kind,
        string id,
        int version
    )
    {
        using var q = Command(
            c,
            tx,
            "SELECT json FROM workflow_catalog WHERE kind=$kind AND id=$id AND version=$version"
        );
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$version", version);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<CatalogEntry>(json, Contract.Json)!
            : null;
    }

    private static CatalogEntry Get(
        SqliteConnection c,
        SqliteTransaction tx,
        string kind,
        string id,
        int version
    )
    {
        ValidateKey(kind, id, version);
        return Find(c, tx, kind, id, version)
            ?? throw new WorkflowException(404, "Catalog version not found.");
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, CatalogEntry entry)
    {
        using var q = Command(
            c,
            tx,
            "INSERT INTO workflow_catalog(kind,id,version,revision,json) VALUES($kind,$id,$version,$revision,$json)"
        );
        Bind(q, entry);
        q.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand q, CatalogEntry entry)
    {
        q.Parameters.AddWithValue("$kind", entry.Kind);
        q.Parameters.AddWithValue("$id", entry.Id);
        q.Parameters.AddWithValue("$version", entry.Version);
        q.Parameters.AddWithValue("$revision", entry.Revision);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, Contract.Json));
    }

    private static void Update(
        SqliteConnection c,
        SqliteTransaction tx,
        CatalogEntry entry,
        int expected
    )
    {
        using var q = Command(
            c,
            tx,
            "UPDATE workflow_catalog SET revision=$revision,json=$json WHERE kind=$kind AND id=$id AND version=$version AND revision=$expected"
        );
        Bind(q, entry);
        q.Parameters.AddWithValue("$expected", expected);
        if (q.ExecuteNonQuery() != 1)
            throw Conflict("Revision changed. Reload the catalog entry.");
    }

    private static void CheckRevision(CatalogEntry entry, int revision)
    {
        if (revision < 1)
            throw Invalid("A positive expected revision is required.");
        if (entry.Revision != revision)
            throw Conflict("Revision changed. Reload the catalog entry.");
    }

    public CatalogEntry Create(CreateCatalogRequest request)
    {
        if (request is null)
            throw Invalid("A catalog definition is required.");
        return Transaction(
            (c, tx) =>
            {
                var entry = new CatalogEntry(
                    request.Kind,
                    request.Id ?? Guid.NewGuid().ToString("N"),
                    1,
                    1,
                    "draft",
                    request.Name,
                    request.Description,
                    request.DocumentType,
                    request.Release,
                    request.Workflow
                );
                entry = FreezeCapabilities(c, tx, entry);
                Validate(c, tx, entry, false);
                using var q = Command(
                    c,
                    tx,
                    "SELECT 1 FROM workflow_catalog WHERE kind=$kind AND id=$id LIMIT 1"
                );
                q.Parameters.AddWithValue("$kind", entry.Kind);
                q.Parameters.AddWithValue("$id", entry.Id);
                if (q.ExecuteScalar() is not null)
                    throw Conflict(
                        "That catalog identity already exists. Create a new version instead."
                    );
                Insert(c, tx, entry);
                return entry;
            }
        );
    }

    public CatalogEntry Edit(string kind, string id, int version, CatalogEntry entry)
    {
        if (entry is null)
            throw Invalid("A catalog definition is required.");
        return Transaction(
            (c, tx) =>
            {
                var current = Get(c, tx, kind, id, version);
                CheckRevision(current, entry.Revision);
                if (current.Status != "draft")
                    throw Conflict("Only draft versions can be edited. Create a new version.");
                if (
                    entry.Kind != kind
                    || entry.Id != id
                    || entry.Version != version
                    || entry.Status != "draft"
                )
                    throw Invalid("The body must match the draft identity, version and status.");
                entry = FreezeCapabilities(c, tx, entry);
                Validate(c, tx, entry, false);
                var saved = entry with { Revision = current.Revision + 1 };
                Update(c, tx, saved, current.Revision);
                return saved;
            }
        );
    }

    public CatalogEntry Publish(string kind, string id, int version, int revision) =>
        Transaction(
            (c, tx) =>
            {
                var current = Get(c, tx, kind, id, version);
                CheckRevision(current, revision);
                if (current.Status != "draft")
                    throw Conflict("Only draft versions can be published.");
                current = FreezeCapabilities(c, tx, current);
                Validate(c, tx, current, true);
                var saved = current with { Status = "published", Revision = current.Revision + 1 };
                Update(c, tx, saved, current.Revision);
                return saved;
            }
        );

    public CatalogEntry Archive(string kind, string id, int version, int revision) =>
        Transaction(
            (c, tx) =>
            {
                var current = Get(c, tx, kind, id, version);
                CheckRevision(current, revision);
                if (current.Status == "archived")
                    throw Conflict("This version is already archived.");
                if (kind == "workflow" && Default(c, tx) == new VersionRef(id, version))
                    throw Conflict(
                        "Select another default workflow before archiving this version."
                    );
                var saved = current with { Status = "archived", Revision = current.Revision + 1 };
                Update(c, tx, saved, current.Revision);
                return saved;
            }
        );

    public CatalogEntry NewVersion(string kind, string id, int version, int revision) =>
        Transaction(
            (c, tx) =>
            {
                var current = Get(c, tx, kind, id, version);
                CheckRevision(current, revision);
                using var q = Command(
                    c,
                    tx,
                    "SELECT MAX(version) FROM workflow_catalog WHERE kind=$kind AND id=$id"
                );
                q.Parameters.AddWithValue("$kind", kind);
                q.Parameters.AddWithValue("$id", id);
                var maximum = Convert.ToInt32(q.ExecuteScalar());
                if (maximum == int.MaxValue)
                    throw Conflict("No more versions can be created for this identity.");
                var saved = current with { Version = maximum + 1, Revision = 1, Status = "draft" };
                Validate(c, tx, saved, false);
                Insert(c, tx, saved);
                return saved;
            }
        );

    public CatalogEntry Clone(string kind, string id, int version, int revision, string name) =>
        Transaction(
            (c, tx) =>
            {
                var current = Get(c, tx, kind, id, version);
                CheckRevision(current, revision);
                var saved = current with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    Revision = 1,
                    Status = "draft",
                    Name = name,
                };
                // Unknown is a reserved fallback identity; cloning it creates an ordinary configurable type.
                Validate(c, tx, saved, false);
                Insert(c, tx, saved);
                return saved;
            }
        );

    public VersionRef SetDefault(VersionRef reference) =>
        Transaction(
            (c, tx) =>
            {
                ValidateReference(reference);
                var entry = Get(c, tx, "workflow", reference.Id, reference.Version);
                if (entry.Status != "published")
                    throw Conflict("The default workflow must be published.");
                SaveSetting(c, tx, "default", reference);
                return reference;
            }
        );

    public WorkflowSnapshot Resolve(
        SqliteConnection c,
        SqliteTransaction tx,
        WorkflowSelection selection
    )
    {
        if (selection is null)
            throw Invalid("A workflow selection is required.");
        if ((selection.WorkflowId is null) != (selection.WorkflowVersion is null))
            throw Invalid("Workflow ID and version must be supplied together.");
        if (
            selection.TestRun
            && (selection.WorkflowId is null || selection.WorkflowRevision is null)
        )
            throw Invalid("Draft preview requires an exact workflow ID, version and revision.");
        if (!selection.TestRun && selection.WorkflowRevision is not null)
            throw Invalid("A workflow revision is only valid for draft preview.");
        var reference = selection.WorkflowId is null
            ? Default(c, tx)
            : new VersionRef(selection.WorkflowId, selection.WorkflowVersion!.Value);
        var entry = Get(c, tx, "workflow", reference.Id, reference.Version);
        if (selection.TestRun)
        {
            CheckRevision(entry, selection.WorkflowRevision!.Value);
            if (entry.Status != "draft")
                throw Conflict("Preview requires a saved draft workflow.");
            if (entry.Workflow!.Mode != "simulation")
                throw Invalid("Draft preview is simulation-only.");
            Validate(c, tx, entry, false);
        }
        else if (entry.Status != "published")
            throw Conflict("Intake requires a published workflow.");

        var config = entry.Workflow!;
        // Called by intake only after checking replay, and using the same acceptance transaction.
        ValidateWorkflow(config);
        if (config.Mode == "live" && (!settings.OcrConfigured || !settings.ClassifierConfigured))
            throw Invalid("Live processing requires both provider credentials.");
        var release = Get(c, tx, "release", config.Release.Id, config.Release.Version);
        var types = release
            .Release!.Types.Select(reference =>
            {
                var type = Get(c, tx, "type", reference.Id, reference.Version);
                var definition = type.DocumentType!;
                return new SnapshotType(
                    type.Id,
                    type.Version,
                    type.Name,
                    type.Description,
                    definition.Keywords,
                    definition.Instructions,
                    definition.Split
                );
            })
            .ToArray();
        var snapshot = new WorkflowSnapshot(
            entry.Id,
            entry.Name,
            entry.Version,
            release.Id,
            release.Version,
            "",
            config.Mode,
            config.ProviderProfile,
            config.Model,
            config.Instructions,
            config.DefaultSplit,
            config.IdentifierKinds,
            config.RequiredIdentifierKinds,
            types,
            Capabilities: PlatformPolicies.Freeze(c, tx, config.Capabilities)
        );
        PlatformPolicies.ValidateTaxonomy(snapshot.Capabilities, types.Select(t => t.Id));
        return WithHash(snapshot with { LegacyRules = UsesOriginalClassification(snapshot) });
    }

    public static Regex CompileRule(string pattern)
    {
        Text(pattern, "Split pattern", 2000, true);
        try
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)
            );
        }
        catch (ArgumentException)
        {
            throw Invalid("Split pattern is not a valid regular expression.");
        }
    }

    public static void ValidatePolicy(SplitPolicy policy, bool allowInherit)
    {
        if (policy is null || !PolicyModes.Contains(policy.Mode))
            throw Invalid("A supported split policy is required.");
        if (!allowInherit && policy.Mode == "inherit")
            throw Invalid("Workflow default split policy cannot inherit.");
        if (policy.PageCount < 1 || policy.PageCount > 10000)
            throw Invalid("Split page count must be between 1 and 10000.");
        Text(policy.Pattern, "Split pattern", 2000);
        if (policy.Mode is "first" or "last" or "identifier")
        {
            var regex = CompileRule(policy.Pattern);
            if (policy.Mode == "identifier" && !regex.GetGroupNumbers().Contains(1))
                throw Invalid("Identifier split pattern requires a first captured group.");
        }
        else if (policy.Pattern.Length > 0)
            throw Invalid("Only marker and identifier split policies accept a pattern.");
        if (policy.Mode != "fixed" && policy.PageCount != 1)
            throw Invalid("Only fixed split policy accepts a page count other than one.");
    }

    private static void Text(string value, string label, int max, bool required = false)
    {
        if (value is null || value.Length > max || (required && string.IsNullOrWhiteSpace(value)))
            throw Invalid(
                $"{label} must be {(required ? "nonempty and " : "")}at most {max} characters."
            );
    }

    private static void ValidateKey(string kind, string id, int version)
    {
        if (kind is not ("type" or "release" or "workflow"))
            throw Invalid("Catalog kind must be type, release or workflow.");
        if (id is null || id.Length > 80 || !IdRule.IsMatch(id) || version < 1)
            throw Invalid("Catalog ID or version is invalid.");
    }

    private static void ValidateReference(VersionRef reference)
    {
        if (reference is null)
            throw Invalid("An exact version reference is required.");
        ValidateKey("type", reference.Id, reference.Version);
    }

    private static void Strings(string[] values, string label, int maxCount, int maxLength)
    {
        if (values is null || values.Length > maxCount)
            throw Invalid($"{label} must be an array with at most {maxCount} entries.");
        foreach (var value in values)
            Text(value, label, maxLength, true);
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw Invalid($"{label} must be unique.");
    }

    private void ValidateWorkflow(WorkflowConfig config)
    {
        if (config is null)
            throw Invalid("A workflow configuration is required.");
        ValidateReference(config.Release);
        if (config.Mode is not ("live" or "simulation") || config.ProviderProfile != "default")
            throw Invalid("A supported mode and provider profile are required.");
        Text(config.Model, "Model", 200, true);
        if (config.Model != (config.Mode == "live" ? settings.Model : Simulation.Model))
            throw Invalid(
                "Model must match the approved model for this provider profile and mode."
            );
        Text(config.Instructions, "Workflow instructions", 8000);
        ValidatePolicy(config.DefaultSplit, false);
        Strings(config.IdentifierKinds, "Identifier kinds", IdentifierKinds.Length, 20);
        Strings(
            config.RequiredIdentifierKinds,
            "Required identifier kinds",
            IdentifierKinds.Length,
            20
        );
        if (
            config.IdentifierKinds.Any(x => !IdentifierKinds.Contains(x))
            || config.RequiredIdentifierKinds.Any(x => !config.IdentifierKinds.Contains(x))
        )
            throw Invalid(
                "Identifier kinds must be supported; required kinds must also be enabled."
            );
    }

    private static CatalogEntry FreezeCapabilities(
        SqliteConnection c,
        SqliteTransaction tx,
        CatalogEntry entry
    ) =>
        entry.Workflow is { Capabilities: not null } workflow
            ? entry with
            {
                Workflow = workflow with
                {
                    Capabilities = PlatformPolicies.Freeze(c, tx, workflow.Capabilities),
                },
            }
            : entry;

    private void Validate(
        SqliteConnection c,
        SqliteTransaction tx,
        CatalogEntry entry,
        bool publishing
    )
    {
        ValidateKey(entry.Kind, entry.Id, entry.Version);
        Text(entry.Name, "Name", 120, true);
        Text(entry.Description, "Description", 2000);
        if (entry.Revision < 1 || entry.Status is not ("draft" or "published" or "archived"))
            throw Invalid("Revision or status is invalid.");
        if (
            (entry.DocumentType is not null ? 1 : 0)
                + (entry.Release is not null ? 1 : 0)
                + (entry.Workflow is not null ? 1 : 0)
            != 1
        )
            throw Invalid("Supply exactly one configuration matching the catalog kind.");
        switch (entry.Kind)
        {
            case "type":
                if (entry.DocumentType is not { } type)
                    throw Invalid("Type configuration is required.");
                Strings(type.Keywords, "Keywords", 64, 200);
                Text(type.Instructions, "Type instructions", 8000);
                ValidatePolicy(type.Split, true);
                if (entry.Id == "unknown" && type.Keywords.Length != 0)
                    throw Invalid("The unknown fallback type cannot have classification keywords.");
                break;
            case "release":
                if (entry.Release?.Types is not { Length: > 0 and <= 64 } references)
                    throw Invalid("A release must contain between 1 and 64 exact type references.");
                foreach (var reference in references)
                    ValidateReference(reference);
                if (
                    references.Select(x => x.Id).Distinct().Count() != references.Length
                    || !references.Any(x => x.Id == "unknown")
                )
                    throw Invalid("Release types must have unique identities and include unknown.");
                foreach (var reference in references)
                    Dependency(c, tx, "type", reference, publishing);
                break;
            case "workflow":
                if (entry.Workflow is not { } workflow)
                    throw Invalid("Workflow configuration is required.");
                ValidateWorkflow(workflow);
                var release = Dependency(c, tx, "release", workflow.Release, publishing);
                var capabilities = PlatformPolicies.Freeze(c, tx, workflow.Capabilities);
                PlatformPolicies.ValidateTaxonomy(
                    capabilities,
                    release.Release!.Types.Select(t => t.Id)
                );
                // Publication checks transitive dependency status, including types archived since release publication.
                Validate(c, tx, release, publishing);
                break;
        }
    }

    private static CatalogEntry Dependency(
        SqliteConnection c,
        SqliteTransaction tx,
        string kind,
        VersionRef reference,
        bool publishing
    )
    {
        var entry = Find(c, tx, kind, reference.Id, reference.Version)
            ?? throw Invalid("A referenced catalog version does not exist.");
        if (publishing && entry.Status != "published")
            throw Conflict("Publish all referenced release and type versions before publication.");
        return entry;
    }

    public WorkflowCatalogResponse Catalog() =>
        Transaction(
            (c, tx) =>
            {
                var entries = new List<CatalogEntry>();
                using (
                    var q = Command(
                        c,
                        tx,
                        "SELECT json FROM workflow_catalog ORDER BY kind,id,version"
                    )
                )
                using (var reader = q.ExecuteReader())
                    while (reader.Read())
                        entries.Add(
                            JsonSerializer.Deserialize<CatalogEntry>(
                                reader.GetString(0),
                                Contract.Json
                            )!
                        );
                var packets = new List<Packet>();
                using (
                    var q = Command(
                        c,
                        tx,
                        "SELECT json FROM packets WHERE COALESCE(json_extract(json,'$.testRun'),0)=0"
                    )
                )
                using (var reader = q.ExecuteReader())
                    while (reader.Read())
                        packets.Add(
                            JsonSerializer.Deserialize<Packet>(reader.GetString(0), Contract.Json)!
                        );
                var history = new Dictionary<string, SortedDictionary<int, Packet>>();
                using (
                    var q = Command(
                        c,
                        tx,
                        "SELECT r.packet_id,r.revision,r.json FROM revisions r JOIN packets p ON p.id=r.packet_id WHERE COALESCE(json_extract(p.json,'$.testRun'),0)=0"
                    )
                )
                using (var reader = q.ExecuteReader())
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        if (!history.TryGetValue(id, out var revisions))
                            history[id] = revisions = [];
                        revisions[reader.GetInt32(1)] = JsonSerializer.Deserialize<Packet>(
                            reader.GetString(2),
                            Contract.Json
                        )!;
                    }
                int Corrections(Packet packet)
                {
                    if (!history.TryGetValue(packet.Id, out var revisions))
                        return 0;
                    revisions[packet.Revision] = packet;
                    return packet.Audit.Count(a =>
                    {
                        if (
                            a.Action
                            is not (
                                "review saved"
                                or "approval invalidated; review saved"
                                or "approved"
                            )
                        )
                            return false;
                        if (!revisions.TryGetValue(a.Revision - 1, out var before))
                            return false;
                        var after = revisions.FirstOrDefault(x => x.Key >= a.Revision).Value;
                        return after is not null
                            && DocumentContent(before) != DocumentContent(after);
                    });
                }
                var grouped = packets
                    .GroupBy(p => new VersionRef(
                        p.EffectiveWorkflow.Id,
                        p.EffectiveWorkflow.Version
                    ))
                    .ToDictionary(g => g.Key, g => g.ToArray());
                var identities = entries
                    .Where(e => e.Kind == "workflow")
                    .Select(e => new VersionRef(e.Id, e.Version))
                    .Concat(grouped.Keys)
                    .Distinct()
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .ThenBy(r => r.Version);
                var stats = identities
                    .Select(reference =>
                    {
                        var runs = grouped.GetValueOrDefault(reference) ?? [];
                        return new WorkflowStats(
                            reference.Id,
                            reference.Version,
                            runs.Length,
                            runs.Count(p => p.Status == "review"),
                            runs.Count(p => p.Status == "failed"),
                            runs.Count(p => p.Status == "approved"),
                            runs.Sum(Corrections)
                        );
                    })
                    .ToArray();
                return new WorkflowCatalogResponse(
                    entries.ToArray(),
                    Default(c, tx),
                    [.. PolicyModes],
                    ["default"],
                    stats,
                    settings.Model
                );
            }
        );

    private static string DocumentContent(Packet packet) =>
        JsonSerializer.Serialize(
            packet.Documents.Select(d => new
            {
                d.Type,
                d.PageIds,
                d.BusinessReference,
            }),
            Contract.Json
        );

    private static SnapshotType[] OriginalTypes() =>
        [
            new(
                "bol",
                1,
                "Bill of lading",
                "Shipment bill of lading.",
                ["bill of lading", "bol"],
                "",
                new()
            ),
            new(
                "pod",
                1,
                "Proof of delivery",
                "Delivery confirmation.",
                ["proof of delivery", "pod"],
                "",
                new()
            ),
            new(
                "packing_list",
                1,
                "Packing list",
                "Itemized shipment contents.",
                ["packing list"],
                "",
                new()
            ),
            new("invoice", 1, "Invoice", "Invoice for goods or services.", ["invoice"], "", new()),
            new("receipt", 1, "Receipt", "Receiving or payment receipt.", ["receipt"], "", new()),
            new(
                "unknown",
                1,
                "Unknown",
                "Unclassified pages requiring manual review.",
                [],
                "",
                new()
            ),
        ];

    private static WorkflowConfig OriginalConfig(string mode, string model) =>
        new(
            new("logistics", 1),
            mode,
            "default",
            mode == "live" ? model : Simulation.Model,
            "",
            new("classifier"),
            [.. IdentifierKinds],
            []
        );

    private static CatalogEntry[] Seeds(string mode, string model)
    {
        var types = OriginalTypes();
        var config = OriginalConfig(mode, model);
        return
        [
            .. types.Select(t => new CatalogEntry(
                "type",
                t.Id,
                1,
                1,
                "published",
                t.Name,
                t.Description,
                DocumentType: new(t.Keywords, t.Instructions, t.Split)
            )),
            new(
                "release",
                "logistics",
                1,
                1,
                "published",
                "Logistics",
                "The six standard logistics document types.",
                Release: new(types.Select(t => new VersionRef(t.Id, t.Version)).ToArray())
            ),
            new(
                "workflow",
                "general-logistics",
                1,
                1,
                "published",
                "General logistics",
                "Classify and review mixed logistics paperwork.",
                Workflow: config
            ),
            new(
                "release",
                "invoice-documents",
                1,
                1,
                "draft",
                "Invoice documents",
                "Invoice and supporting packing-list documents.",
                Release: new([new("invoice", 1), new("packing_list", 1), new("unknown", 1)])
            ),
            new(
                "workflow",
                "invoice-processing",
                1,
                1,
                "draft",
                "Invoice processing",
                "Review invoices and capture order references.",
                Workflow: config with
                {
                    Release = new("invoice-documents", 1),
                    Mode = "simulation",
                    Model = Simulation.Model,
                    Instructions =
                        "Identify invoices and supporting packing lists. Preserve unrecognized pages for review.",
                    DefaultSplit = new("separate"),
                    IdentifierKinds = ["order", "shipment"],
                    RequiredIdentifierKinds = ["order"],
                }
            ),
            new(
                "release",
                "receiving-documents",
                1,
                1,
                "draft",
                "Receiving documents",
                "Receiving receipts and shipment paperwork.",
                Release: new([
                    new("receipt", 1),
                    new("bol", 1),
                    new("packing_list", 1),
                    new("pod", 1),
                    new("unknown", 1),
                ])
            ),
            new(
                "workflow",
                "receiving",
                1,
                1,
                "draft",
                "Receiving",
                "Review receiving paperwork and capture receipt references.",
                Workflow: config with
                {
                    Release = new("receiving-documents", 1),
                    Mode = "simulation",
                    Model = Simulation.Model,
                    Instructions =
                        "Identify receiving receipts and supporting shipment documents. Verify each receipt reference.",
                    IdentifierKinds = ["receipt", "shipment", "bol"],
                    RequiredIdentifierKinds = ["receipt"],
                }
            ),
        ];
    }

    private static bool UsesOriginalClassification(WorkflowSnapshot snapshot)
    {
        // Copying a workflow or changing assembly settings must not silently change classification.
        // Exact original classification definitions retain historical word/whitespace rules.
        static string Classification(SnapshotType[] types) =>
            JsonSerializer.Serialize(
                types
                    .OrderBy(t => t.Id, StringComparer.Ordinal)
                    .Select(t => new
                    {
                        t.Id,
                        t.Name,
                        t.Description,
                        t.Keywords,
                        t.Instructions,
                    }),
                Contract.Json
            );
        return snapshot.Instructions == ""
            && Classification(snapshot.Types) == Classification(OriginalTypes());
    }

    private static WorkflowSnapshot WithHash(WorkflowSnapshot snapshot) =>
        snapshot with
        {
            Hash = Convert
                .ToHexString(
                    SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(
                            snapshot with
                            {
                                Hash = "",
                            },
                            Contract.Json
                        )
                    )
                )
                .ToLowerInvariant(),
        };

    public static WorkflowSnapshot Legacy(string mode = "simulation", string? model = null)
    {
        // Legacy runs have no retained provider settings. This stable, non-secret fallback performs no I/O.
        if (mode is not ("simulation" or "live" or "demo"))
            mode = "simulation";
        var config = OriginalConfig(mode, model ?? "jev-latest");
        return WithHash(
            new(
                "legacy-logistics",
                "Legacy logistics",
                1,
                "logistics",
                1,
                "",
                mode,
                config.ProviderProfile,
                config.Model,
                config.Instructions,
                config.DefaultSplit,
                config.IdentifierKinds,
                config.RequiredIdentifierKinds,
                OriginalTypes(),
                true
            )
        );
    }

    private T Transaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var result = action(c, tx);
        tx.Commit();
        return result;
    }
}
