using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public static class PlatformExportEvidence
{
    public static object Read(Store store, Packet packet)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        var reference = Rows(
            c,
            tx,
            "reference_results",
            "packet_id=$id AND revision=$revision",
            packet
        );
        var extraction = Rows(
            c,
            tx,
            "extraction_objects",
            "kind='run' AND json_extract(json,'$.packetId')=$id AND json_extract(json,'$.packetRevision')=$revision",
            packet
        );
        var routing = Rows(
            c,
            tx,
            "quality_decisions",
            "packet_id=$id AND revision <= $revision ORDER BY revision DESC LIMIT 1",
            packet
        );
        var flow = Rows(c, tx, "flow_blocks", "packet_id=$id AND snapshot_hash=$flow", packet);
        return new
        {
            packetId = packet.Id,
            packet.Revision,
            workflowHash = packet.EffectiveWorkflow.Hash,
            packet.TestRun,
            referenceEvidence = reference,
            extractionRuns = extraction,
            initialQualityRouting = routing,
            flowExecutions = flow,
        };
    }

    private static JsonElement[] Rows(
        SqliteConnection c,
        SqliteTransaction tx,
        string table,
        string predicate,
        Packet packet
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        q.Parameters.AddWithValue("$table", table);
        if (q.ExecuteScalar() is null)
            return [];
        q.Parameters.Clear();
        // Table names and predicates are internal constants, never caller-provided SQL.
        q.CommandText = $"SELECT json FROM {table} WHERE {predicate}";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        q.Parameters.AddWithValue(
            "$flow",
            $"{packet.EffectiveWorkflow.Hash}:revision:{packet.Revision}"
        );
        using var r = q.ExecuteReader();
        var rows = new List<JsonElement>();
        while (r.Read())
        {
            using var document = JsonDocument.Parse(r.GetString(0));
            rows.Add(document.RootElement.Clone());
        }
        return rows.ToArray();
    }
}
