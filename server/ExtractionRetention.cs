using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class ExtractionRetentionParticipant(Store store) : IOperationsRetentionParticipant
{
    public string Name => "extraction-recognition-preprocessing-output";
    public string[] Tables => ["extraction_objects"];

    public string[] Blockers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet
    )
    {
        var outputDirectory = Path.Combine(store.Root, "outputs");
        if (Directory.Exists(outputDirectory))
            foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.identity.json"))
            {
                try
                {
                    var identity = System.Text.Json.JsonSerializer.Deserialize<OutputFileIdentity>(
                        File.ReadAllBytes(path),
                        Contract.Json
                    );
                    if (identity == null)
                        return
                        [
                            "An output identity cannot be verified; explicit file accounting is required before privacy purge.",
                        ];
                    if (identity.PacketId == packet.Id)
                        return
                        [
                            "Packet has a retained output file identity, including possible interrupted output. Verified file cleanup is required before privacy purge.",
                        ];
                }
                catch (System.Text.Json.JsonException)
                {
                    return
                    [
                        "An interrupted output identity requires explicit file accounting before privacy purge.",
                    ];
                }
            }
        if (!ExtractionStorage.Exists(connection, transaction))
            return [];
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText =
            "SELECT kind FROM extraction_objects WHERE json_extract(json,'$.packetId')=$packet AND kind IN ('derivative','output') LIMIT 1";
        q.Parameters.AddWithValue("$packet", packet.Id);
        if (q.ExecuteScalar() is string)
            return
            [
                "Packet has immutable derivative/output files. Explicit verified file cleanup is required before privacy purge.",
            ];
        q.CommandText =
            "SELECT 1 FROM extraction_objects WHERE kind='template' AND json_extract(json,'$.sourceSha256') IN (SELECT json_extract(value,'$.sha256') FROM json_each($sources)) LIMIT 1";
        q.Parameters.Clear();
        q.Parameters.AddWithValue(
            "$sources",
            System.Text.Json.JsonSerializer.Serialize(packet.Sources, Contract.Json)
        );
        return q.ExecuteScalar() != null
            ? ["Packet source is retained by an immutable recognition template."]
            : [];
    }

    public void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet)
    {
        if (!ExtractionStorage.Exists(connection, transaction))
            return;
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText =
            "DELETE FROM extraction_objects WHERE json_extract(json,'$.packetId')=$packet AND kind IN ('run','correction','recognition')";
        q.Parameters.AddWithValue("$packet", packet.Id);
        q.ExecuteNonQuery();
    }
}
