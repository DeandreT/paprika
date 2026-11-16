using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class ConnectorRetentionParticipant : IOperationsRetentionParticipant
{
    public string Name => "connectors";
    public string[] Tables => ["connector_jobs"];

    public string[] Blockers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet
    )
    {
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "SELECT COUNT(*) FROM connector_jobs WHERE packet_id=$id";
        q.Parameters.AddWithValue("$id", packet.Id);
        if (Convert.ToInt64(q.ExecuteScalar()) > 0)
            return
            [
                "Connector delivery ledger and external output copies require explicit retention reconciliation.",
            ];
        q.CommandText =
            "SELECT COUNT(*) FROM intake_keys WHERE packet_id=$id AND key LIKE 'connector:%'";
        return Convert.ToInt64(q.ExecuteScalar()) > 0
            ? ["Connector intake idempotency ledger must be retained to prevent duplicate import."]
            : [];
    }

    public void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet) { }
}
