using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class QualityRetentionParticipant : IOperationsRetentionParticipant
{
    public string Name => "quality-training";
    public string[] Tables =>
        [
            "quality_decisions",
            "quality_qa",
            "training_datasets",
            "training_examples",
            "training_models",
            "training_predictions",
        ];

    public string[] Blockers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet
    )
    {
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText =
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM training_examples WHERE json_extract(json,'$.packetId')=$id) AND EXISTS(SELECT 1 FROM training_models) THEN 1 ELSE 0 END";
        q.Parameters.AddWithValue("$id", packet.Id);
        // Conservative: token weights may encode this source, and models can be embedded in old snapshots.
        return Convert.ToInt32(q.ExecuteScalar()) == 1
            ?
            [
                "Retained trained models may derive from this packet's examples; model/snapshot privacy review is required before physical purge.",
            ]
            : [];
    }

    public void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet)
    {
        if (Blockers(connection, transaction, packet).Length > 0)
            throw new WorkflowException(409, "Training provenance blocks physical purge.");
        foreach (var table in new[] { "quality_decisions", "quality_qa", "training_predictions" })
        {
            using var q = connection.CreateCommand();
            q.Transaction = transaction;
            q.CommandText = $"DELETE FROM {table} WHERE packet_id=$id";
            q.Parameters.AddWithValue("$id", packet.Id);
            q.ExecuteNonQuery();
        }
        using var examples = connection.CreateCommand();
        examples.Transaction = transaction;
        examples.CommandText =
            "DELETE FROM training_examples WHERE json_extract(json,'$.packetId')=$id";
        examples.Parameters.AddWithValue("$id", packet.Id);
        examples.ExecuteNonQuery();
    }
}
