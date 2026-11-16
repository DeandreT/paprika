using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class ReferenceRetentionParticipant : IOperationsRetentionParticipant
{
    public string Name => "references";
    public string[] Tables =>
        ["reference_datasets", "reference_results", "reference_connector_bindings"];

    public string[] Blockers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet
    ) => [];

    public void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet)
    {
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "DELETE FROM reference_results WHERE packet_id=$id";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.ExecuteNonQuery();
    }
}
