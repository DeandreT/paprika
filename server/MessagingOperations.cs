using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

namespace PaperworkClassifier;

public sealed record QueueSummary(string Name, int Pending);

public sealed record DeadLetterSummary(
    string Id,
    string MessageType,
    string? PacketId,
    string? DeliveryId,
    DateTimeOffset FailedAt,
    string Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
        int? PacketRevision = null
);

public sealed record MessagingSummary(
    string Mode,
    QueueSummary[] Queues,
    DeadLetterSummary[] DeadLetters
);

public sealed record ReplayResponse(string Id, string Status);

public sealed class MessagingOperations(Store store, DurableQueue queue, IWolverineRuntime runtime)
{
    private IDeadLetters Letters => runtime.Storage.DeadLetters;

    public async Task<MessagingSummary> Read(CancellationToken ct)
    {
        var summaries = new List<DeadLetterSummary>();
        var archive = new List<Guid>();
        var seen = new HashSet<Guid>();
        using var c = store.Open();
        for (var page = 1; summaries.Count < 100; page++)
        {
            var result = await Letters.QueryAsync(
                new DeadLetterEnvelopeQuery { PageNumber = page, PageSize = 100 },
                ct
            );
            foreach (var letter in result.Envelopes)
            {
                if (!seen.Add(letter.Id) || letter.ArchivedAt is not null)
                    continue;
                // Read the claim, generation and business state from the same snapshot.
                using var tx = c.BeginTransaction(deferred: true);
                using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "SELECT 1 FROM replayed_letters WHERE id=$id";
                q.Parameters.AddWithValue("$id", letter.Id.ToString());
                if (q.ExecuteScalar() is not null)
                {
                    archive.Add(letter.Id);
                    continue;
                }
                var message = Message(letter);
                var (kind, id, workId) = message switch
                {
                    ProcessPacket p => ("packet", p.PacketId, p.WorkId),
                    ProcessDelivery d => ("delivery", d.DeliveryId, d.WorkId),
                    ContinuePlatformFlow flow => ("continuation", flow.PacketId, flow.WorkId),
                    _ => (null, null, null),
                };
                if (workId is null)
                    continue;
                var current = DurableQueue.Current(c, tx, kind!, id!);
                if (current != workId)
                {
                    // Only a known replacement generation proves this letter is superseded.
                    if (current is not null)
                        archive.Add(letter.Id);
                    continue;
                }
                var status =
                    kind == "packet" ? Store.Read(c, tx, id!)?.Status
                    : kind == "continuation" ? Store.Read(c, tx, id!)?.Status
                    : DeliveryStore.Read(c, tx, "WHERE id=$id", ("$id", id!))?.Status;
                // A current generation can still be finalizing. Status alone never authorizes archival.
                var continuationFailed =
                    message is ContinuePlatformFlow continuation
                    && status == "approved"
                    && Store.Read(c, tx, id!)?.Revision == continuation.Revision
                    && ContinuationWork.Failed(c, tx, continuation);
                if (
                    (message is ContinuePlatformFlow ? !continuationFailed : status != "failed")
                    || letter.Replayable
                    || letter.FailedAt is not { } failedAt
                )
                    continue;
                summaries.Add(
                    new(
                        letter.Id.ToString(),
                        message!.GetType().Name,
                        (message as ProcessPacket)?.PacketId
                            ?? (message as ContinuePlatformFlow)?.PacketId,
                        (message as ProcessDelivery)?.DeliveryId,
                        failedAt,
                        letter.ExceptionType.Contains(
                            nameof(TransientWorkException),
                            StringComparison.Ordinal
                        )
                            ? "Transient operation failed after the bounded automatic retries. Correct the cause before replay."
                            : "Operation failed. Check configuration and source files before replay.",
                        (message as ContinuePlatformFlow)?.Revision
                    )
                );
                if (summaries.Count == 100)
                    break;
            }
            if (result.Envelopes.Count < 100)
                break;
        }
        // Archiving during offset pagination would shift later pages and skip eligible letters.
        // Claims and superseded generations stay safe to archive even if business status changes.
        try
        {
            foreach (var ids in archive.Chunk(100))
                await Letters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery(ids), ct);
        }
        catch (Exception)
        {
            runtime
                .Services.GetRequiredService<ILogger<MessagingOperations>>()
                .LogWarning("Dead letter archival deferred; eligible failures remain visible.");
        }
        // Counts include persisted ready, in-flight, and scheduled retry inbox rows, excluding handled dedup receipts.
        var queues = new[] { "processing", "delivery", "continuation" }
            .Select(name =>
            {
                using var q = c.CreateCommand();
                q.CommandText =
                    "SELECT COUNT(*) FROM wolverine_incoming_envelopes WHERE received_at=$address AND status != 'Handled'";
                q.Parameters.AddWithValue("$address", "local://" + name + "/");
                return new QueueSummary(name, Convert.ToInt32(q.ExecuteScalar()));
            })
            .ToArray();
        return new("durable-local", queues, summaries.ToArray());
    }

    public async Task<int> Replay(Guid id, CancellationToken ct)
    {
        var letter = await Letters.DeadLetterEnvelopeByIdAsync(id);
        if (letter is null)
            return 404;
        if (letter.ArchivedAt is not null || letter.Replayable)
            return 409;
        var message = Message(letter);
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT 1 FROM replayed_letters WHERE id=$id";
        q.Parameters.AddWithValue("$id", id.ToString());
        if (q.ExecuteScalar() is not null)
            return 409;
        switch (message)
        {
            case ProcessPacket p:
                var packet = Store.Read(c, tx, p.PacketId);
                if (
                    packet?.Status != "failed"
                    || p.WorkId is null
                    || DurableQueue.Current(c, tx, "packet", p.PacketId) != p.WorkId
                )
                    return 409;
                packet.Status = "queued";
                packet.Error = null;
                packet.Audit.Add(
                    new(DateTimeOffset.UtcNow, "dead letter replay requested", packet.Revision)
                );
                Store.Write(c, tx, packet);
                break;
            case ProcessDelivery d:
                var delivery = DeliveryStore.Read(c, tx, "WHERE id=$id", ("$id", d.DeliveryId));
                if (
                    delivery?.Status != "failed"
                    || d.WorkId is null
                    || DurableQueue.Current(c, tx, "delivery", d.DeliveryId) != d.WorkId
                )
                    return 409;
                DeliveryStore.Save(
                    c,
                    tx,
                    delivery with
                    {
                        Status = "queued",
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }
                );
                break;
            case ContinuePlatformFlow flow:
                var approved = Store.Read(c, tx, flow.PacketId);
                if (
                    approved?.Status != "approved"
                    || approved.Revision != flow.Revision
                    || flow.WorkId is null
                    || DurableQueue.Current(c, tx, "continuation", flow.PacketId) != flow.WorkId
                    || !ContinuationWork.Failed(c, tx, flow)
                )
                    return 409;
                if (!ResetContinuationBlocks(c, tx, approved))
                    return 409;
                approved.Audit.Add(
                    new(
                        DateTimeOffset.UtcNow,
                        "approved continuation dead letter replay requested",
                        approved.Revision
                    )
                );
                Store.Write(c, tx, approved);
                break;
            default:
                return 409;
        }
        q.CommandText = "INSERT INTO replayed_letters VALUES($id,$at)";
        q.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        q.ExecuteNonQuery();
        // Replay is a new generation. Business state, replay claim and replacement Wolverine envelope
        // commit together. The original remains a genuine Wolverine dead letter, archived via its API.
        // A crash before archival cannot permit repeat replay or reuse an old exhausted envelope.
        await queue.CommitAsync(tx, [message]);
        try
        {
            await Letters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery([id]), ct);
        }
        catch (Exception)
        {
            runtime
                .Services.GetRequiredService<ILogger<MessagingOperations>>()
                .LogWarning(
                    "Replay committed; original dead letter archival deferred. The durable replay claim prevents duplicate replay."
                );
        }
        return 200;
    }

    private static object? Message(DeadLetterEnvelope letter)
    {
        if (letter.Envelope.Data is not { } data)
            return null;
        try
        {
            if (letter.MessageType.Contains(nameof(ProcessPacket), StringComparison.Ordinal))
                return JsonSerializer.Deserialize<ProcessPacket>(data, Contract.Json);
            if (letter.MessageType.Contains(nameof(ProcessDelivery), StringComparison.Ordinal))
                return JsonSerializer.Deserialize<ProcessDelivery>(data, Contract.Json);
            if (letter.MessageType.Contains(nameof(ContinuePlatformFlow), StringComparison.Ordinal))
                return JsonSerializer.Deserialize<ContinuePlatformFlow>(data, Contract.Json);
        }
        catch (JsonException) { }
        return null;
    }

    private static bool ResetContinuationBlocks(
        Microsoft.Data.Sqlite.SqliteConnection c,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        Packet packet
    )
    {
        if (packet.EffectiveWorkflow.Capabilities?.Flow is null)
            return true;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT block_id,json FROM flow_blocks WHERE packet_id=$id AND snapshot_hash=$hash";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue(
            "$hash",
            $"{packet.EffectiveWorkflow.Hash}:revision:{packet.Revision}"
        );
        using var reader = q.ExecuteReader();
        var blocks = new List<(string Id, FlowExecution Execution)>();
        while (reader.Read())
            blocks.Add(
                (
                    reader.GetString(0),
                    JsonSerializer.Deserialize<FlowExecution>(reader.GetString(1), Contract.Json)!
                )
            );
        reader.Close();
        if (
            blocks.Any(b =>
                b.Execution.Status == "running"
                && b.Execution.UpdatedAt >= DateTimeOffset.UtcNow.AddMinutes(-15)
            )
        )
            return false;
        foreach (var block in blocks.Where(b => b.Execution.Status is "failed" or "running"))
        {
            using var update = c.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                "UPDATE flow_blocks SET json=$json WHERE packet_id=$id AND snapshot_hash=$hash AND block_id=$block";
            update.Parameters.AddWithValue("$id", packet.Id);
            update.Parameters.AddWithValue(
                "$hash",
                $"{packet.EffectiveWorkflow.Hash}:revision:{packet.Revision}"
            );
            update.Parameters.AddWithValue("$block", block.Id);
            update.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(
                    block.Execution with
                    {
                        Status = "pending",
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    Contract.Json
                )
            );
            update.ExecuteNonQuery();
        }
        return true;
    }
}

public static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessaging(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Messaging")
            .WithMetadata(
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 403),
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 500)
            );
        api.MapGet("/messaging", async (MessagingOperations operations, CancellationToken ct) =>
                TypedResults.Ok(await operations.Read(ct))
            )
            .WithName("GetMessaging")
            .WithSummary(
                "Inspect persisted durable queue counts and up to 100 sanitized dead letters"
            );
        api.MapPost(
                "/messaging/deadletters/{id}/replay",
                async Task<Results<Ok<ReplayResponse>, NotFound<ApiError>, Conflict<ApiError>>> (
                    string id,
                    MessagingOperations operations,
                    CancellationToken ct
                ) =>
                {
                    if (!Guid.TryParse(id, out var guid))
                        return TypedResults.NotFound(new ApiError("Dead letter not found."));
                    return await operations.Replay(guid, ct) switch
                    {
                        200 => TypedResults.Ok(new ReplayResponse(id, "queued")),
                        404 => TypedResults.NotFound(new ApiError("Dead letter not found.")),
                        _ => TypedResults.Conflict(
                            new ApiError(
                                "This dead letter is stale, already replayed, or its work is no longer failed."
                            )
                        ),
                    };
                }
            )
            .WithName("ReplayDeadLetter")
            .WithSummary(
                "Atomically queue one new attempt for a failed current-generation dead letter"
            );
        return app;
    }
}
