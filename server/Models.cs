using System.Text.Json;

namespace paprika;

public sealed record SourceIdentifier(string SourceId, string Kind, string Value, string Evidence);

public sealed class Packet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string Mode { get; set; } = "simulation";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Revision { get; set; } = 1;
    public string? ParentPacketId { get; set; }
    public int RunNumber { get; set; } = 1;
    public List<IdentifierCandidate> IdentifierCandidates { get; set; } = [];
    public List<SourceIdentifier> SourceIdentifiers { get; set; } = [];
    public string? Error { get; set; }
    public List<Page> Pages { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
    public Processing? Processing { get; set; }
    public List<Audit> Audit { get; set; } = [];
    public List<Source> Sources { get; set; } = [];
    public string? PromptVersion { get; set; }
    public string? TaxonomyVersion { get; set; }
    public WorkflowSnapshot? Workflow { get; set; }
    public bool TestRun { get; set; }
    public WorkflowSelection? IntakeSelection { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public WorkflowSnapshot EffectiveWorkflow => Workflow ?? WorkflowRegistry.Legacy(Mode);
}

public sealed class Page
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int PageNumber { get; set; }
    public string PreviewUrl { get; set; } = "";
    public string Text { get; set; } = "";
    public string PredictedType { get; set; } = "unknown";
    public double Confidence { get; set; }
    public double StartProbability { get; set; }
    public List<string> ReviewReasons { get; set; } = [];
}

public sealed class Document
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "unknown";
    public List<string> PageIds { get; set; } = [];
    public string BusinessReference { get; set; } = "";
    public List<string> ReviewReasons { get; set; } = [];
}

public sealed class Source
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public int PageCount { get; set; }
    public string? Sha256 { get; set; }
    public string? OperationUrl { get; set; }
    public string? OcrJson { get; set; }
}

public sealed record Audit(
    DateTimeOffset At,
    string Action,
    int Revision,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
        string? Actor = null
);

public sealed record Processing(string Stage, string? Model = null, long? ElapsedMs = null);

public sealed record ReviewRequest(int Revision, List<Document>? Documents, bool Approve);

public sealed record RevisionRequest(int Revision);

public sealed record ReprocessRequest(
    int Revision,
    string? WorkflowId = null,
    int? WorkflowVersion = null
);

public sealed record IdentifierCandidate(
    string Kind,
    string Value,
    List<string> PageIds,
    string Evidence
);

public static class Contract
{
    public const string TaxonomyVersion = "logistics-poc-1";
    public static readonly string[] Types =
    [
        "bol",
        "pod",
        "packing_list",
        "invoice",
        "receipt",
        "unknown",
    ];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string? Validate(Packet p, ReviewRequest r)
    {
        if (r.Documents is not { Count: > 0 })
            return "At least one document is required.";
        if (
            r.Documents.Any(d =>
                d is null
                || !p.EffectiveWorkflow.Types.Any(t => t.Id == d.Type)
                || d.PageIds is not { Count: > 0 }
                || string.IsNullOrWhiteSpace(d.Id)
                || d.Id.Length > 100
                || d.BusinessReference is null
                || d.BusinessReference.Length > 500
            )
        )
            return "Each document needs a valid type, ID and pages.";
        if (r.Documents.Select(d => d.Id).Distinct().Count() != r.Documents.Count)
            return "Document IDs must be unique.";
        var ids = r.Documents.SelectMany(d => d.PageIds).ToArray();
        if (
            ids.Length != p.Pages.Count
            || ids.Distinct().Count() != ids.Length
            || !ids.ToHashSet().SetEquals(p.Pages.Select(x => x.Id))
        )
            return "Every source page must appear exactly once.";
        return null;
    }
}
