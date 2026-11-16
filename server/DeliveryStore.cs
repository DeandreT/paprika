using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed record QueueDeliveryRequest(int Revision, string Scenario = "success");

public sealed record DeliveryResponse(
    string Id,
    string PacketId,
    int Revision,
    string Status,
    string Mode,
    string Scenario,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error,
    string? ArtifactUrl,
    string? Sha256,
    string? SupersedesDeliveryId,
    WorkflowSnapshot? Workflow = null,
    bool TestRun = false
);

public sealed record DeliveryResult(DeliveryResponse? Delivery, int Code, string? Error);

// This outbox is a local transport simulator. No network transport is registered here.
public sealed class DeliveryStore
{
    private readonly Store store;
    private readonly string root;

    public DeliveryStore(Store store)
    {
        this.store = store;
        root = Path.Combine(store.Root, "deliveries");
        Directory.CreateDirectory(root);
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS deliveries(id TEXT PRIMARY KEY, packet_id TEXT NOT NULL, revision INTEGER NOT NULL, response TEXT NOT NULL, snapshot TEXT NOT NULL, UNIQUE(packet_id,revision));";
        q.ExecuteNonQuery();
        var legacy = Path.Combine(root, "outbox.sqlite");
        if (File.Exists(legacy))
        {
            using var attach = c.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS legacy";
            attach.Parameters.AddWithValue("$path", legacy);
            attach.ExecuteNonQuery();
            using var migration = c.BeginTransaction();
            using var copy = c.CreateCommand();
            copy.Transaction = migration;
            copy.CommandText = "INSERT OR IGNORE INTO deliveries SELECT * FROM legacy.deliveries";
            copy.ExecuteNonQuery();
            migration.Commit();
            using var detach = c.CreateCommand();
            detach.CommandText = "DETACH DATABASE legacy";
            detach.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        return store.Open();
    }

    internal static DeliveryResponse? Read(
        SqliteConnection c,
        SqliteTransaction? tx,
        string where,
        params (string, object)[] values
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT response FROM deliveries " + where;
        foreach (var (key, value) in values)
            q.Parameters.AddWithValue(key, value);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<DeliveryResponse>(json, Contract.Json)
            : null;
    }

    public DeliveryResponse? Get(string id)
    {
        using var c = Open();
        return Read(c, null, "WHERE id=$id", ("$id", id));
    }

    public DeliveryResponse[] All(string? packetId = null)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT response FROM deliveries "
            + (packetId is null ? "" : "WHERE packet_id=$packet ")
            + "ORDER BY rowid DESC";
        if (packetId is not null)
            q.Parameters.AddWithValue("$packet", packetId);
        using var r = q.ExecuteReader();
        var results = new List<DeliveryResponse>();
        while (r.Read())
            results.Add(
                JsonSerializer.Deserialize<DeliveryResponse>(r.GetString(0), Contract.Json)!
            );
        return results.ToArray();
    }

    public DeliveryResult Queue(string packetId, QueueDeliveryRequest request)
    {
        if (
            request.Revision < 1
            || request.Scenario
                is not ("success" or "transient-once" or "transient-always" or "reject")
        )
            return new(
                null,
                400,
                "A positive revision and a supported simulation scenario are required."
            );
        using var c = Open();
        using var tx = c.BeginTransaction();
        var packet = Store.Read(c, tx, packetId);
        if (packet is null)
            return new(null, 404, "Packet not found.");
        if (packet.TestRun)
            return new(
                null,
                409,
                "Test runs cannot be delivered. Publish the workflow and create a production run."
            );
        var existing = Read(
            c,
            tx,
            "WHERE packet_id=$packet AND revision=$revision",
            ("$packet", packetId),
            ("$revision", request.Revision)
        );
        if (existing is not null)
            return existing.Scenario == request.Scenario
                ? new(existing, 200, null)
                : new(null, 409, "This revision already has a delivery with a different scenario.");
        if (packet.Status != "approved" || packet.Revision != request.Revision)
            return new(null, 409, "The current approved packet revision is required.");
        var prior = Read(
            c,
            tx,
            "WHERE packet_id=$packet AND revision<$revision ORDER BY revision DESC LIMIT 1",
            ("$packet", packetId),
            ("$revision", request.Revision)
        );
        var now = DateTimeOffset.UtcNow;
        var delivery = new DeliveryResponse(
            Guid.NewGuid().ToString("N"),
            packetId,
            packet.Revision,
            "queued",
            "simulation",
            request.Scenario,
            0,
            now,
            now,
            null,
            null,
            null,
            prior?.Id,
            packet.EffectiveWorkflow,
            packet.TestRun
        );
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO deliveries VALUES($id,$packet,$revision,$response,$snapshot)";
        q.Parameters.AddWithValue("$id", delivery.Id);
        q.Parameters.AddWithValue("$packet", packetId);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        q.Parameters.AddWithValue("$response", JsonSerializer.Serialize(delivery, Contract.Json));
        q.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(packet, Contract.Json));
        q.ExecuteNonQuery();
        store.Commit(tx, new ProcessDelivery(delivery.Id));
        return new(delivery, 200, null);
    }

    internal static void Save(SqliteConnection c, SqliteTransaction tx, DeliveryResponse delivery)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "UPDATE deliveries SET response=$response WHERE id=$id";
        q.Parameters.AddWithValue("$id", delivery.Id);
        q.Parameters.AddWithValue("$response", JsonSerializer.Serialize(delivery, Contract.Json));
        q.ExecuteNonQuery();
    }

    public DeliveryResult Retry(string id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var d = Read(c, tx, "WHERE id=$id", ("$id", id));
        if (d is null)
            return new(null, 404, "Delivery not found.");
        if (d.Status != "failed")
            return new(
                null,
                409,
                "Only failed deliveries can be retried. Rejections are terminal."
            );
        d = d with { Status = "queued", Error = null, UpdatedAt = DateTimeOffset.UtcNow };
        Save(c, tx, d);
        store.Commit(tx, new ProcessDelivery(d.Id));
        return new(d, 200, null);
    }

    public string ArtifactPath(DeliveryResponse d) => Path.Combine(root, d.Id + ".zip");

    public bool Process(string deliveryId)
    {
        {
            var d = Get(deliveryId);
            if (d is null)
                return false;
            using var c = Open();
            using (var tx = c.BeginTransaction())
            {
                d = Read(c, tx, "WHERE id=$id", ("$id", d.Id))!;
                if (d.Status is not ("queued" or "processing"))
                    return true;
                d = d with
                {
                    Status = "processing",
                    Attempts = d.Attempts + 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                Save(c, tx, d);
                tx.Commit();
            }
            try
            {
                var path = ArtifactPath(d);
                if (!File.Exists(path))
                {
                    if (d.Scenario == "reject")
                        d = d with
                        {
                            Status = "rejected",
                            Error =
                                "Simulated business rejection. No external system was contacted.",
                        };
                    else if (
                        d.Scenario == "transient-always"
                        || (d.Scenario == "transient-once" && d.Attempts == 1)
                    )
                        throw new IOException("Simulated transient delivery failure.");
                    else
                    {
                        using var q = c.CreateCommand();
                        q.CommandText = "SELECT snapshot FROM deliveries WHERE id=$id";
                        q.Parameters.AddWithValue("$id", d.Id);
                        var snapshot = JsonSerializer.Deserialize<Packet>(
                            (string)q.ExecuteScalar()!,
                            Contract.Json
                        )!;
                        var temporary = Path.Combine(
                            root,
                            d.Id + "." + Guid.NewGuid().ToString("N") + ".tmp"
                        );
                        using (
                            var file = new FileStream(
                                temporary,
                                FileMode.CreateNew,
                                FileAccess.ReadWrite,
                                FileShare.None
                            )
                        )
                        {
                            file.Write(Pdfs.Export(snapshot, store));
                            file.Position = 0;
                            using (var zip = new ZipArchive(file, ZipArchiveMode.Update, true))
                            using (var receipt = zip.CreateEntry("delivery-receipt.json").Open())
                                JsonSerializer.Serialize(
                                    receipt,
                                    new
                                    {
                                        mode = "simulation",
                                        transport = "simulation",
                                        deliveryId = d.Id,
                                        packetId = d.PacketId,
                                        revision = d.Revision,
                                        workflow = snapshot.EffectiveWorkflow,
                                        snapshot.TestRun,
                                        externalSystemContacted = false,
                                        message = "Local artifact only; not a downstream delivery acknowledgment.",
                                    },
                                    Contract.Json
                                );
                            file.Flush(true);
                        }
                        File.Move(temporary, path);
                    }
                }
                if (File.Exists(path))
                {
                    using var zip = ZipFile.OpenRead(path);
                    using var receipt = JsonDocument.Parse(
                        zip.GetEntry("delivery-receipt.json")!.Open()
                    );
                    if (
                        receipt.RootElement.GetProperty("deliveryId").GetString() != d.Id
                        || receipt.RootElement.GetProperty("revision").GetInt32() != d.Revision
                        || receipt.RootElement.GetProperty("transport").GetString() != "simulation"
                    )
                        throw new InvalidDataException();
                    d = d with
                    {
                        Status = "delivered",
                        Error = null,
                        Sha256 = Pdfs.SourceHash(path),
                        ArtifactUrl = $"/api/deliveries/{d.Id}/artifact",
                    };
                }
            }
            catch
            {
                throw;
            }
            using (var tx = c.BeginTransaction())
            {
                Save(c, tx, d with { UpdatedAt = DateTimeOffset.UtcNow });
                tx.Commit();
            }
            return true;
        }
    }
}
