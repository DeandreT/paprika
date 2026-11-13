using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public sealed class CaseFlowRetention : IOperationsRetentionParticipant
{
    public string Name => "cases-and-flows";
    public string[] Tables =>
        [
            "platform_cases",
            "platform_case_history",
            "case_members",
            "case_proposals",
            "flow_runs",
            "flow_blocks",
            "flow_library",
        ];

    public string[] Blockers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet
    )
    {
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "SELECT count(*) FROM case_members WHERE packet_id=$p";
        q.Parameters.AddWithValue("$p", packet.Id);
        var blockers = new List<string>();
        if (Convert.ToInt64(q.ExecuteScalar()) > 0)
            blockers.Add("Packet is retained as an explicit case member.");
        q.CommandText =
            "SELECT count(*) FROM platform_case_history h,json_each(h.json,'$.members') m WHERE json_extract(m.value,'$.packetId')=$p";
        if (Convert.ToInt64(q.ExecuteScalar()) > 0)
            blockers.Add("Packet source provenance is retained in immutable case history.");
        q.CommandText =
            "SELECT count(*) FROM flow_blocks WHERE packet_id=$p AND snapshot_hash=$h AND json_extract(json,'$.status') IN ('running','pending','waitingApproval','failed')";
        q.Parameters.AddWithValue(
            "$h",
            $"{packet.EffectiveWorkflow.Hash}:revision:{packet.Revision}"
        );
        if (Convert.ToInt64(q.ExecuteScalar()) > 0)
            blockers.Add("Packet has active flow work.");
        return blockers.ToArray();
    }

    public void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet)
    {
        if (Blockers(connection, transaction, packet).Length > 0)
            throw new WorkflowException(409, "Case or flow dependencies block purge.");
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.Parameters.AddWithValue("$p", packet.Id);
        q.CommandText =
            "DELETE FROM case_proposals WHERE packet_id=$p; DELETE FROM flow_blocks WHERE packet_id=$p; DELETE FROM flow_runs WHERE packet_id=$p;";
        q.ExecuteNonQuery();
    }
}
