using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace paprika;

internal sealed record ApprovedFieldExport(
    int SchemaVersion,
    string PacketId,
    int Revision,
    string WorkflowHash,
    ApprovedFieldExport.DefinitionIdentity Definition,
    ApprovedFieldExport.ExportedDocument[] Documents
)
{
    internal const int CurrentSchemaVersion = 1;

    internal sealed record DefinitionIdentity(string Id, int Version, string Sha256);

    internal sealed record ExportedDocument(string DocumentId, string Type, ExportedField[] Fields);

    internal sealed record ExportedField(
        string Name,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
        string Status,
        EvidenceReference[] Evidence
    );

    internal sealed record EvidenceReference(
        string PageId,
        string SourceId,
        int PageNumber,
        int Offset,
        int Length
    );

    // The release gate has validated the exact configured run and its evidence in this transaction.
    internal static ApprovedFieldExport? Read(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet
    )
    {
        var workflow = packet.EffectiveWorkflow;
        if (
            packet.Status != "approved"
            || packet.Revision < 1
            || workflow.Capabilities?.Extraction is not { } policy
        )
            return null;

        var definition = policy.Definition!;
        var run = ExtractionPlatform.Current(db, tx, packet, policy.Sha256);
        var pages = packet.Pages.ToDictionary(page => page.Id);
        ExportedField[] Fields(Document document)
        {
            if (definition.DocumentType is not null && definition.DocumentType != document.Type)
                return [];

            var result = run!.Results.Single(r => r.DocumentId == document.Id);
            return definition
                .Fields.Select(field =>
                {
                    var value = result.Fields.Single(v => v.Name == field.Name);
                    var references = value
                        .Evidence.Select(evidence =>
                        {
                            var page = pages[evidence.PageId];
                            return new EvidenceReference(
                                page.Id,
                                page.SourceId,
                                page.PageNumber,
                                evidence.Offset,
                                evidence.Length
                            );
                        })
                        .ToArray();
                    return new ExportedField(
                        field.Name,
                        field.Kind,
                        NormalizeValue(document, field, value),
                        value.Status,
                        references
                    );
                })
                .ToArray();
        }

        return new(
            CurrentSchemaVersion,
            packet.Id,
            packet.Revision,
            workflow.Hash,
            new(policy.DefinitionId, policy.Version, policy.Sha256),
            packet
                .Documents.Select(doc => new ExportedDocument(doc.Id, doc.Type, Fields(doc)))
                .ToArray()
        );
    }

    private static string? NormalizeValue(
        Document document,
        ExtractionField field,
        ExtractionValue value
    )
    {
        // A reviewer can intentionally clear an optional field. Keep its correction status.
        if (
            value.Status == "missing"
            || (
                value.Status == "corrected"
                && !field.Required
                && string.IsNullOrWhiteSpace(value.Value)
            )
        )
            return null;
        if (
            value.Status is ("extracted" or "corrected")
            && !string.IsNullOrWhiteSpace(value.Value)
            && ExtractionPlatform.Normalize(value.Value, field.Kind) is { } normalized
        )
            return normalized;

        // The release gate can allow unusable values for optional fields.
        throw new WorkflowException(
            409,
            $"Extraction field {field.Name} requires review for document {document.Id}."
        );
    }
}
