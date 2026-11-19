using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class ConnectorDeliveryStore
{
    private readonly Store store;
    private readonly ConnectorCatalog catalog;
    private readonly ReferenceService references;

    public ConnectorDeliveryStore(
        Store store,
        ConnectorCatalog catalog,
        ReferenceService references
    )
    {
        this.store = store;
        this.catalog = catalog;
        this.references = references;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS connector_jobs(id TEXT PRIMARY KEY,connector_id TEXT NOT NULL,packet_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,payload BLOB NOT NULL,next_at TEXT NOT NULL,UNIQUE(connector_id,packet_id,revision));";
        q.ExecuteNonQuery();
    }

    public ConnectorDelivery[] All(string? packetId = null)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM connector_jobs"
            + (packetId is null ? "" : " WHERE packet_id=$packet")
            + " ORDER BY rowid DESC";
        if (packetId is not null)
            q.Parameters.AddWithValue("$packet", packetId);
        using var r = q.ExecuteReader();
        var rows = new List<ConnectorDelivery>();
        while (r.Read())
            rows.Add(JsonSerializer.Deserialize<ConnectorDelivery>(r.GetString(0), Contract.Json)!);
        return rows.ToArray();
    }

    public ConnectorDelivery Queue(
        string connectorId,
        ConnectorDeliveryRequest request,
        string? actor = null
    )
    {
        var config = catalog.Configuration(connectorId);
        if (config["Kind"] is not ("folderExport" or "httpDelivery"))
            throw new InvalidDataException("Connector does not deliver.");
        if (config["Kind"] == "folderExport")
            catalog.Root(connectorId, "folderExport");
        else
        {
            if (!bool.TryParse(config["SupportsIdempotency"], out var supported) || !supported)
                throw new InvalidDataException(
                    "HTTP delivery requires a receiver explicitly configured to honor Idempotency-Key."
                );
            var pair = catalog.Request(connectorId, "httpDelivery", HttpMethod.Post);
            pair.Client.Dispose();
            pair.Request.Dispose();
        }
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var packet = Store.Read(c, tx, request.PacketId)
            ?? throw new KeyNotFoundException("Packet not found.");
        RequireApproved(packet, request.Revision);
        var referenceEvidence = references
            .Results(packet.Id)
            .Where(row =>
                row.Revision == packet.Revision
                && row.WorkflowHash == packet.EffectiveWorkflow.Hash
                && packet.Documents.Any(doc =>
                    doc.Id == row.DocumentId && doc.BusinessReference == row.Reference
                )
            )
            .ToArray();
        var payload = Pdfs.ApprovedExport(c, tx, packet, store);
        if (referenceEvidence.Length > 0)
        {
            using var bytes = new MemoryStream();
            bytes.Write(payload);
            using (
                var archive = new System.IO.Compression.ZipArchive(
                    bytes,
                    System.IO.Compression.ZipArchiveMode.Update,
                    true
                )
            )
            using (var evidence = archive.CreateEntry("reference-provenance.json").Open())
                JsonSerializer.Serialize(
                    evidence,
                    new
                    {
                        packetId = packet.Id,
                        packet.Revision,
                        workflowHash = packet.EffectiveWorkflow.Hash,
                        synthetic = referenceEvidence.Any(row => row.Synthetic),
                        references = referenceEvidence.Select(row => new
                        {
                            row.DocumentId,
                            row.Reference,
                            row.Status,
                            row.Synthetic,
                            row.Provenance,
                            row.DatasetHash,
                            row.ResultHash,
                        }),
                    },
                    Contract.Json
                );
            payload = bytes.ToArray();
        }
        if (payload.Length > 50 * 1024 * 1024)
            throw new InvalidDataException("Export exceeds 50 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM connector_jobs WHERE connector_id=$connector AND packet_id=$packet AND revision=$revision";
        q.Parameters.AddWithValue("$connector", connectorId);
        q.Parameters.AddWithValue("$packet", packet.Id);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        if (q.ExecuteScalar() is string existing)
        {
            tx.Commit();
            return JsonSerializer.Deserialize<ConnectorDelivery>(existing, Contract.Json)!;
        }
        var id = Guid.NewGuid().ToString("N");
        var job = new ConnectorDelivery(
            id,
            connectorId,
            packet.Id,
            packet.Revision,
            "paperwork-" + id,
            hash,
            "queued",
            0,
            false,
            "pending",
            null,
            DateTimeOffset.UtcNow,
            null,
            packet.EffectiveWorkflow.Hash,
            actor,
            ConnectorBindingHash: catalog.BindingHash(connectorId),
            SyntheticReferenceEvidence: referenceEvidence.Any(row => row.Synthetic)
        );
        q.CommandText =
            "INSERT INTO connector_jobs VALUES($id,$connector,$packet,$revision,$json,$payload,$next)";
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, Contract.Json));
        q.Parameters.AddWithValue("$payload", payload);
        q.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.ToString("O"));
        q.ExecuteNonQuery();
        tx.Commit();
        return job;
    }

    private void RequireApproved(Packet packet, int revision)
    {
        if (packet.Revision != revision || packet.Status != "approved")
            throw new InvalidOperationException(
                "Delivery requires the exact current approved revision."
            );
        if (packet.TestRun)
            throw new InvalidOperationException("Test runs cannot use actual connector delivery.");
        if (!references.IsPolicyCurrent(packet))
            throw new InvalidOperationException("Pinned reference destination changed.");
        if (
            references
                .Results(packet.Id)
                .Any(row =>
                    row.Revision == packet.Revision
                    && row.WorkflowHash == packet.EffectiveWorkflow.Hash
                    && row.Synthetic
                )
        )
            throw new InvalidOperationException(
                "Synthetic reference evidence cannot authorize actual connector delivery."
            );
        if (
            packet.EffectiveWorkflow.Capabilities?.Reference?.RequireVerified == true
            && !references.IsVerified(packet, allowSynthetic: false)
        )
            throw new InvalidOperationException(
                "Current revision requires verified business references."
            );
    }

    private static ConnectorDelivery? Read(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM connector_jobs WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<ConnectorDelivery>(json, Contract.Json)
            : null;
    }

    private static void Write(
        SqliteConnection c,
        SqliteTransaction tx,
        ConnectorDelivery job,
        DateTimeOffset next
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "UPDATE connector_jobs SET json=$json,next_at=$next WHERE id=$id";
        q.Parameters.AddWithValue("$id", job.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, Contract.Json));
        q.Parameters.AddWithValue("$next", next.ToString("O"));
        q.ExecuteNonQuery();
    }

    public ConnectorDelivery Retry(string id)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var job = Read(c, tx, id) ?? throw new KeyNotFoundException("Delivery not found.");
        if (job.Status != "failed")
            throw new InvalidOperationException(
                "Only a failed delivery can be retried with its original idempotency key."
            );
        var packet = Store.Read(c, tx, job.PacketId)
            ?? throw new KeyNotFoundException("Packet not found.");
        RequireApproved(packet, job.Revision);
        job = job with { Status = "queued", Error = null };
        Write(c, tx, job, DateTimeOffset.UtcNow);
        tx.Commit();
        return job;
    }

    public ConnectorDelivery Receipt(
        string id,
        ConnectorReceiptRequest request,
        string? actor = null
    )
    {
        if (
            request.BusinessStatus is not ("accepted" or "rejected")
            || string.IsNullOrWhiteSpace(request.ReceiptId)
            || request.ReceiptId.Length > 200
        )
            throw new InvalidDataException(
                "Receipt needs accepted/rejected status and a bounded receipt ID."
            );
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var job = Read(c, tx, id) ?? throw new KeyNotFoundException("Delivery not found.");
        if (!job.TransportAcknowledged)
            throw new InvalidOperationException("Transport has not acknowledged delivery.");
        if (
            job.BusinessStatus != "pending"
            && (job.BusinessStatus != request.BusinessStatus || job.ReceiptId != request.ReceiptId)
        )
            throw new InvalidOperationException("Business receipt is immutable.");
        job = job with
        {
            BusinessStatus = request.BusinessStatus,
            ReceiptId = request.ReceiptId,
            ReceiptRecordedBy = actor,
        };
        Write(c, tx, job, DateTimeOffset.UtcNow);
        tx.Commit();
        return job;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        // SQLite's write transaction serializes workers and review changes through the side effect.
        // A crash rolls back this attempt; receiver/folder idempotency makes replay safe.
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT id,payload FROM connector_jobs WHERE json_extract(json,'$.status')='queued' AND next_at <= $now ORDER BY rowid LIMIT 1";
        q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        using var reader = q.ExecuteReader();
        if (!reader.Read())
        {
            tx.Commit();
            return false;
        }
        var id = reader.GetString(0);
        var payload = (byte[])reader.GetValue(1);
        reader.Close();
        var job = Read(c, tx, id)!;
        try
        {
            var packet = Store.Read(c, tx, job.PacketId)
                ?? throw new InvalidOperationException("Packet is unavailable.");
            RequireApproved(packet, job.Revision);
            if (packet.EffectiveWorkflow.Hash != job.WorkflowHash)
                throw new InvalidOperationException("Workflow changed.");
            if (Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() != job.ContentHash)
                throw new InvalidDataException("Stored content hash changed.");
            var config = catalog.Configuration(job.ConnectorId);
            if (catalog.BindingHash(job.ConnectorId) != job.ConnectorBindingHash)
                throw new InvalidDataException(
                    "Connector destination configuration changed; queue a newly approved revision."
                );
            if (config["Kind"] == "folderExport")
            {
                var root = catalog.Root(job.ConnectorId, "folderExport");
                var target = Path.Combine(root, job.IdempotencyKey + ".zip");
                if (File.Exists(target))
                {
                    if (
                        (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0
                        || new FileInfo(target).Length != payload.Length
                        || Pdfs.SourceHash(target) != job.ContentHash
                    )
                        throw new InvalidDataException(
                            "Existing output does not match delivery content."
                        );
                }
                else
                {
                    var temporary = Path.Combine(
                        root,
                        "." + job.Id + "-" + Guid.NewGuid().ToString("N") + ".tmp"
                    );
                    try
                    {
                        await using (
                            var output = new FileStream(
                                temporary,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None,
                                81920,
                                FileOptions.WriteThrough
                            )
                        )
                        {
                            await output.WriteAsync(payload, ct);
                            await output.FlushAsync(ct);
                            output.Flush(true);
                        }
                        File.Move(temporary, target, false);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }
            }
            else if (config["Kind"] == "httpDelivery")
            {
                if (config["SupportsIdempotency"] != "true")
                    throw new InvalidDataException("Receiver idempotency approval is required.");
                var pair = catalog.Request(job.ConnectorId, "httpDelivery", HttpMethod.Post);
                using var client = pair.Client;
                using var request = pair.Request;
                request.Headers.Add("Idempotency-Key", job.IdempotencyKey);
                request.Headers.Add("X-Content-SHA256", job.ContentHash);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new("application/zip");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                );
                await ConnectorCatalog.BoundedAsync(response.Content, 64 * 1024, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Delivery transport did not acknowledge.");
            }
            else
                throw new InvalidDataException("Configured delivery kind changed.");
            job = job with
            {
                Status = "delivered",
                Attempts = job.Attempts + 1,
                TransportAcknowledged = true,
                Error = null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or HttpRequestException
                        or TaskCanceledException
                        or UnauthorizedAccessException
            )
        {
            var attempts = job.Attempts + 1;
            var terminal = ex is InvalidOperationException or InvalidDataException || attempts >= 5;
            job = job with
            {
                Status = terminal ? "failed" : "queued",
                Attempts = attempts,
                Error =
                    ex is InvalidOperationException
                        ? "Approval or reference verification is no longer current."
                        : "Connector attempt failed; inspect server configuration and destination.",
            };
        }
        Write(
            c,
            tx,
            job,
            DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, job.Attempts)))
        );
        tx.Commit();
        return true;
    }
}

public sealed class ConnectorWorker(
    ConnectorDeliveryStore deliveries,
    ILogger<ConnectorWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await deliveries.ProcessNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Connector worker paused after a local ledger failure.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
