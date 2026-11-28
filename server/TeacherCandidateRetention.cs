using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class TeacherCandidateRetention : IOperationsRetentionParticipant
{
    public string Name => "reviewed-teacher-labels";
    public string[] Tables => ["teacher_observations", "teacher_reviews", "teacher_model_sources"];

    public string[] Blockers(SqliteConnection db, SqliteTransaction tx, Packet packet)
    {
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT 1 FROM teacher_model_sources WHERE packet_id=$packet LIMIT 1";
        q.Parameters.AddWithValue("$packet", packet.Id);
        return q.ExecuteScalar() is not null
            ?
            [
                "A retained model used this packet's teacher labels. Model and snapshot review is required before purge.",
            ]
            : [];
    }

    public void Purge(SqliteConnection db, SqliteTransaction tx, Packet packet)
    {
        if (Blockers(db, tx, packet).Length > 0)
            throw new WorkflowException(
                409,
                "Retained models depend on this packet's teacher evidence."
            );
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            DELETE FROM teacher_reviews WHERE EXISTS
                (SELECT 1 FROM json_each(membership) member WHERE json_extract(member.value,'$[0]')=$packet);
            DELETE FROM teacher_observations WHERE packet_id=$packet;
            """;
        q.Parameters.AddWithValue("$packet", packet.Id);
        q.ExecuteNonQuery();
    }
}
