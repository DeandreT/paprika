using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public sealed record OperationsTaskEvent(
    DateTimeOffset At,
    string Actor,
    string Action,
    int Revision,
    string? Outcome = null
);

public sealed record OperationsTask(
    string Id,
    string PacketId,
    string WorkflowId,
    int PacketRevision,
    int Revision,
    string State,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    string? ClaimedBy,
    DateTimeOffset? LeaseUntil,
    string? Outcome,
    List<OperationsTaskEvent> History,
    int LeaseSeconds = 900
);

public sealed record OperationsTaskChange(
    int Revision,
    int? LeaseSeconds = null,
    string? Outcome = null
);

public sealed record OperationsTaskSchedule(int Revision, int Priority, DateTimeOffset DueAt);

public sealed record OperationsTaskStats(
    int Total,
    int Available,
    int Claimed,
    int Completed,
    int Overdue
);

public sealed record OperationsRetentionPolicy(
    int MinimumAgeDays = 90,
    bool ExecutionEnabled = false
);

public sealed record OperationsRetentionCandidate(
    string PacketId,
    int Revision,
    bool Eligible,
    string[] Reasons
);

public sealed record OperationsRetentionRequest(string PacketId, int Revision, bool ConfirmArchive);

public sealed record OperationsPurgeRequest(
    string PacketId,
    int Revision,
    bool ConfirmPermanentDeletion
);

public sealed record OperationsPurgePlan(string PacketId, bool Eligible, string[] Blockers);

public interface IOperationsRetentionParticipant
{
    string Name { get; }
    string[] Tables { get; }
    string[] Blockers(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
    void Purge(SqliteConnection connection, SqliteTransaction transaction, Packet packet);
}

public sealed record OperationsTombstone(
    string PacketId,
    int PacketRevision,
    string WorkflowId,
    DateTimeOffset At,
    string Actor,
    string Action
);

public sealed class OperationsConflict(string message, int statusCode = 409) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>SQLite transactions serialize claims, lease transitions and stale revision checks across processes.</summary>
public sealed class OperationsPlatformStore
{
    private readonly Store store;
    private readonly IConfiguration config;
    private readonly IOperationsRetentionParticipant[] participants;

    public OperationsPlatformStore(
        Store store,
        IConfiguration config,
        IEnumerable<IOperationsRetentionParticipant>? participants = null
    )
    {
        this.store = store;
        this.config = config;
        this.participants = participants?.ToArray() ?? [];
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS operations_tasks(id TEXT PRIMARY KEY,packet_id TEXT NOT NULL,packet_revision INTEGER NOT NULL,json TEXT NOT NULL,UNIQUE(packet_id,packet_revision)); CREATE TABLE IF NOT EXISTS operations_tombstones(packet_id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS operations_retention_audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT,json TEXT NOT NULL);";
        q.ExecuteNonQuery();
    }

    public OperationsRetentionPolicy RetentionPolicy =>
        new(
            Math.Max(1, config.GetValue("Paperwork:Operations:Retention:MinimumAgeDays", 90)),
            config.GetValue<bool>("Paperwork:Operations:Retention:ExecutionEnabled")
        );

    public string? ValidateReview(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet,
        string actor
    )
    {
        if (
            !string.Equals(
                config["Paperwork:Operations:Mode"],
                "shared",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return null;
        using var q = Command(
            connection,
            transaction,
            "SELECT json FROM operations_tasks WHERE packet_id=$id AND packet_revision=$revision",
            ("$id", packet.Id),
            ("$revision", packet.Revision)
        );
        var task = q.ExecuteScalar() is string json ? Decode<OperationsTask>(json) : null;
        return
            task is not null
            && task.State == "claimed"
            && task.ClaimedBy == actor
            && task.LeaseUntil > DateTimeOffset.UtcNow
            ? null
            : "Claim the current review task before changing this packet.";
    }

    public string? ValidateMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Packet packet,
        string actor
    )
    {
        if (
            !string.Equals(
                config["Paperwork:Operations:Mode"],
                "shared",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return null;
        if (packet.Status == "review")
            return ValidateReview(connection, transaction, packet, actor);
        using var q = Command(
            connection,
            transaction,
            "SELECT json FROM operations_tasks WHERE packet_id=$id AND packet_revision=$revision",
            ("$id", packet.Id),
            ("$revision", packet.Revision)
        );
        var task = q.ExecuteScalar() is string json ? Decode<OperationsTask>(json) : null;
        return
            task is { State: "claimed" }
            && task.LeaseUntil > DateTimeOffset.UtcNow
            && task.ClaimedBy != actor
            ? "Another reviewer has an active claim on this packet."
            : null;
    }

    public void RecordReview(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string packetId,
        int packetRevision,
        string actor,
        string outcome
    )
    {
        using var q = Command(
            connection,
            transaction,
            "SELECT json FROM operations_tasks WHERE packet_id=$id AND packet_revision=$revision",
            ("$id", packetId),
            ("$revision", packetRevision)
        );
        if (q.ExecuteScalar() is not string json)
            return;
        var task = Decode<OperationsTask>(json);
        if (task.State is "completed" or "superseded")
            return;
        task = Event(
            task with
            {
                State = "completed",
                ClaimedBy = null,
                LeaseUntil = null,
                Outcome = outcome,
            },
            DateTimeOffset.UtcNow,
            actor,
            "review saved",
            outcome
        );
        Save(connection, transaction, task);
    }

    private static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Contract.Json)!;

    private static SqliteCommand Command(
        SqliteConnection c,
        SqliteTransaction? tx,
        string sql,
        params (string, object?)[] parameters
    )
    {
        var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        foreach (var (key, value) in parameters)
            q.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return q;
    }

    private static List<OperationsTask> ReadTasks(SqliteConnection c, SqliteTransaction? tx)
    {
        using var q = Command(c, tx, "SELECT json FROM operations_tasks");
        using var r = q.ExecuteReader();
        var tasks = new List<OperationsTask>();
        while (r.Read())
            tasks.Add(Decode<OperationsTask>(r.GetString(0)));
        return tasks;
    }

    private static void Save(SqliteConnection c, SqliteTransaction tx, OperationsTask task)
    {
        using var q = Command(
            c,
            tx,
            "UPDATE operations_tasks SET json=$json WHERE id=$id",
            ("$json", JsonSerializer.Serialize(task, Contract.Json)),
            ("$id", task.Id)
        );
        q.ExecuteNonQuery();
    }

    private static OperationsTask Event(
        OperationsTask task,
        DateTimeOffset now,
        string actor,
        string action,
        string? outcome = null
    )
    {
        var revision = task.Revision + 1;
        return task with
        {
            Revision = revision,
            History = [.. task.History, new(now, actor, action, revision, outcome)],
        };
    }

    private static OperationsTask Expire(OperationsTask task, DateTimeOffset now) =>
        task.State == "claimed" && task.LeaseUntil <= now
            ? Event(
                task with
                {
                    State = "available",
                    ClaimedBy = null,
                    LeaseUntil = null,
                },
                now,
                "system",
                "lease expired"
            )
            : task;

    public List<OperationsTask> Tasks(OperationsIdentity identity, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        // Synchronization is persistent and idempotent, including one task for each new review revision.
        using (
            var q = Command(
                c,
                tx,
                "SELECT json FROM packets WHERE json_extract(json,'$.status')='review' AND id NOT IN (SELECT packet_id FROM operations_tombstones)"
            )
        )
        {
            using var reader = q.ExecuteReader();
            var packets = new List<Packet>();
            while (reader.Read())
                packets.Add(Decode<Packet>(reader.GetString(0)));
            reader.Close();
            foreach (var p in packets)
            {
                var policy = p.EffectiveWorkflow.Capabilities?.Operations;
                var task = new OperationsTask(
                    Guid.NewGuid().ToString("N"),
                    p.Id,
                    p.EffectiveWorkflow.Id,
                    p.Revision,
                    1,
                    "available",
                    policy?.DefaultPriority ?? 0,
                    now,
                    p.CreatedAt.AddHours(
                        policy?.ReviewSlaHours
                            ?? Math.Clamp(
                                config.GetValue("Paperwork:Operations:ReviewSlaHours", 24),
                                1,
                                8760
                            )
                    ),
                    null,
                    null,
                    null,
                    [new(now, "system", "created", 1)],
                    policy?.LeaseSeconds ?? 900
                );
                using var insert = Command(
                    c,
                    tx,
                    "INSERT OR IGNORE INTO operations_tasks VALUES($id,$packet,$revision,$json)",
                    ("$id", task.Id),
                    ("$packet", p.Id),
                    ("$revision", p.Revision),
                    ("$json", JsonSerializer.Serialize(task, Contract.Json))
                );
                insert.ExecuteNonQuery();
            }
        }
        var tasks = ReadTasks(c, tx);
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = Expire(tasks[i], now);
            using var q = Command(
                c,
                tx,
                "SELECT json FROM packets WHERE id=$id",
                ("$id", task.PacketId)
            );
            var packet = q.ExecuteScalar() is string json ? Decode<Packet>(json) : null;
            if (
                task.State is "available" or "claimed"
                && (
                    packet is null
                    || packet.Status is not ("review" or "approved")
                    || packet.Revision != task.PacketRevision
                )
            )
                task = Event(
                    task with
                    {
                        State = "superseded",
                        ClaimedBy = null,
                        LeaseUntil = null,
                        Outcome = "packet changed",
                    },
                    now,
                    "system",
                    "superseded"
                );
            if (task != tasks[i])
                Save(c, tx, task);
            tasks[i] = task;
        }
        tx.Commit();
        return tasks
            .Where(t => identity.CanRead(t.WorkflowId))
            .OrderBy(t => t.State is "completed" or "superseded")
            .ThenByDescending(t => t.Priority)
            .ThenBy(t => t.DueAt)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }

    public OperationsTask Change(
        string id,
        OperationsTaskChange request,
        OperationsIdentity identity,
        string action,
        DateTimeOffset? at = null
    )
    {
        if (request.LeaseSeconds is < 30 or > 3600)
            throw new OperationsConflict("Lease must be between 30 and 3600 seconds.", 400);
        if (
            action == "complete"
            && (string.IsNullOrWhiteSpace(request.Outcome) || request.Outcome.Length > 1000)
        )
            throw new OperationsConflict(
                "A completion outcome of 1 to 1000 characters is required.",
                400
            );
        var now = at ?? DateTimeOffset.UtcNow;
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(c, tx, "SELECT json FROM operations_tasks WHERE id=$id", ("$id", id));
        var task = q.ExecuteScalar() is string json
            ? Decode<OperationsTask>(json)
            : throw new OperationsConflict("Task not found.", 404);
        if (!identity.CanReview(task.WorkflowId))
            throw new OperationsConflict("Task not found.", 404);
        var leaseSeconds = request.LeaseSeconds ?? task.LeaseSeconds;
        var expired = Expire(task, now);
        if (expired != task)
        {
            Save(c, tx, expired);
            tx.Commit();
            throw new OperationsConflict(
                "Lease expired. Reload the task before claiming it again."
            );
        }
        if (task.Revision != request.Revision)
            throw new OperationsConflict("Task revision changed. Reload the task.");
        using var packetQuery = Command(
            c,
            tx,
            "SELECT json FROM packets WHERE id=$id AND id NOT IN (SELECT packet_id FROM operations_tombstones)",
            ("$id", task.PacketId)
        );
        var packet = packetQuery.ExecuteScalar() is string packetJson
            ? Decode<Packet>(packetJson)
            : null;
        if (
            packet is null
            || packet.Status is not ("review" or "approved")
            || packet.Revision != task.PacketRevision
        )
            throw new OperationsConflict("Packet review revision changed.");
        if (action == "claim")
        {
            if (task.State != "available")
                throw new OperationsConflict("Task is not available.");
            task = task with
            {
                State = "claimed",
                ClaimedBy = identity.Name,
                LeaseUntil = now.AddSeconds(leaseSeconds),
            };
        }
        else
        {
            if (task.State != "claimed" || task.ClaimedBy != identity.Name)
                throw new OperationsConflict("Only the current lease owner can change this task.");
            task = action switch
            {
                "renew" => task with { LeaseUntil = now.AddSeconds(leaseSeconds) },
                "release" => task with { State = "available", ClaimedBy = null, LeaseUntil = null },
                "complete" => task with
                {
                    State = "completed",
                    ClaimedBy = null,
                    LeaseUntil = null,
                    Outcome = request.Outcome,
                },
                _ => throw new OperationsConflict("Unknown task action.", 400),
            };
        }
        task = Event(task, now, identity.Name, action, request.Outcome);
        Save(c, tx, task);
        tx.Commit();
        return task;
    }

    public OperationsTask Schedule(
        string id,
        OperationsTaskSchedule request,
        OperationsIdentity identity
    )
    {
        if (!identity.Administrator)
            throw new OperationsConflict("Administrator access required.", 403);
        if (request.Priority is < 0 or > 100)
            throw new OperationsConflict("Priority must be between 0 and 100.", 400);
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(c, tx, "SELECT json FROM operations_tasks WHERE id=$id", ("$id", id));
        var task = q.ExecuteScalar() is string json
            ? Decode<OperationsTask>(json)
            : throw new OperationsConflict("Task not found.", 404);
        if (task.Revision != request.Revision || task.State is "completed" or "superseded")
            throw new OperationsConflict("Task changed or is closed.");
        task = Event(
            task with
            {
                Priority = request.Priority,
                DueAt = request.DueAt,
            },
            DateTimeOffset.UtcNow,
            identity.Name,
            "scheduled"
        );
        Save(c, tx, task);
        tx.Commit();
        return task;
    }

    public OperationsTask OpenReview(string packetId, int revision, OperationsIdentity identity)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(
            c,
            tx,
            "SELECT json FROM packets WHERE id=$id AND id NOT IN (SELECT packet_id FROM operations_tombstones)",
            ("$id", packetId)
        );
        var packet = q.ExecuteScalar() is string json
            ? Decode<Packet>(json)
            : throw new OperationsConflict("Packet not found.", 404);
        if (!identity.CanReview(packet.EffectiveWorkflow.Id))
            throw new OperationsConflict("Packet not found.", 404);
        if (packet.Revision != revision || packet.Status is not ("review" or "approved"))
            throw new OperationsConflict("Packet changed or is not ready for review.");
        using var existingQuery = Command(
            c,
            tx,
            "SELECT json FROM operations_tasks WHERE packet_id=$id AND packet_revision=$revision",
            ("$id", packetId),
            ("$revision", revision)
        );
        var existing = existingQuery.ExecuteScalar() is string existingJson
            ? Decode<OperationsTask>(existingJson)
            : null;
        var now = DateTimeOffset.UtcNow;
        var policy = packet.EffectiveWorkflow.Capabilities?.Operations;
        var task = existing is null
            ? new OperationsTask(
                Guid.NewGuid().ToString("N"),
                packet.Id,
                packet.EffectiveWorkflow.Id,
                packet.Revision,
                1,
                "available",
                policy?.DefaultPriority ?? 0,
                now,
                now.AddHours(policy?.ReviewSlaHours ?? 24),
                null,
                null,
                null,
                [new(now, identity.Name, "review requested", 1)],
                policy?.LeaseSeconds ?? 900
            )
            : existing;
        if (existing is null)
        {
            using var insert = Command(
                c,
                tx,
                "INSERT INTO operations_tasks VALUES($id,$packet,$revision,$json)",
                ("$id", task.Id),
                ("$packet", packet.Id),
                ("$revision", revision),
                ("$json", JsonSerializer.Serialize(task, Contract.Json))
            );
            insert.ExecuteNonQuery();
        }
        else if (task.State is "completed" or "superseded")
        {
            task = Event(
                task with
                {
                    State = "available",
                    Outcome = null,
                    ClaimedBy = null,
                    LeaseUntil = null,
                },
                now,
                identity.Name,
                "review reopened"
            );
            Save(c, tx, task);
        }
        tx.Commit();
        return task;
    }

    public bool IsArchived(string packetId)
    {
        using var c = store.Open();
        using var q = Command(
            c,
            null,
            "SELECT 1 FROM operations_tombstones WHERE packet_id=$id",
            ("$id", packetId)
        );
        return q.ExecuteScalar() is not null;
    }

    public List<OperationsTombstone> Tombstones()
    {
        using var c = store.Open();
        using var q = Command(
            c,
            null,
            "SELECT json FROM operations_tombstones ORDER BY rowid DESC"
        );
        using var reader = q.ExecuteReader();
        var result = new List<OperationsTombstone>();
        while (reader.Read())
            result.Add(Decode<OperationsTombstone>(reader.GetString(0)));
        return result;
    }

    public List<OperationsTombstone> RetentionAudit()
    {
        using var c = store.Open();
        using var q = Command(
            c,
            null,
            "SELECT json FROM operations_retention_audit ORDER BY sequence DESC"
        );
        using var reader = q.ExecuteReader();
        var result = new List<OperationsTombstone>();
        while (reader.Read())
            result.Add(Decode<OperationsTombstone>(reader.GetString(0)));
        return result;
    }

    private static void AuditRetention(
        SqliteConnection c,
        SqliteTransaction tx,
        OperationsTombstone entry
    )
    {
        using var q = Command(
            c,
            tx,
            "INSERT INTO operations_retention_audit(json) VALUES($json)",
            ("$json", JsonSerializer.Serialize(entry, Contract.Json))
        );
        q.ExecuteNonQuery();
    }

    private OperationsRetentionCandidate Candidate(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        DateTimeOffset now
    )
    {
        var reasons = new List<string>();
        if (packet.Status is not ("approved" or "failed" or "cancelled" or "canceled"))
            reasons.Add("Packet is active or awaiting review.");
        if (
            packet.CreatedAt > now.AddDays(-RetentionPolicy.MinimumAgeDays)
            || packet.Audit.Any(a => a.At > now.AddDays(-RetentionPolicy.MinimumAgeDays))
        )
            reasons.Add("Minimum age since creation or last recorded activity has not elapsed.");
        using (
            var q = Command(
                c,
                tx,
                "SELECT json FROM operations_tasks WHERE packet_id=$id",
                ("$id", packet.Id)
            )
        )
        {
            using var r = q.ExecuteReader();
            while (r.Read())
                if (Decode<OperationsTask>(r.GetString(0)).State is "available" or "claimed")
                {
                    reasons.Add("An open review task depends on this packet.");
                    break;
                }
        }
        using (
            var q = Command(
                c,
                tx,
                "SELECT COUNT(*) FROM packets WHERE json_extract(json,'$.parentPacketId')=$id",
                ("$id", packet.Id)
            )
        )
            if (Convert.ToInt64(q.ExecuteScalar()) > 0)
                reasons.Add("A reprocess/document-history descendant depends on this packet.");
        if (HasDurableDependency(c, tx, packet))
            reasons.Add("Active or replayable durable work depends on this packet.");
        using (
            var q = Command(
                c,
                tx,
                "SELECT 1 FROM operations_tombstones WHERE packet_id=$id",
                ("$id", packet.Id)
            )
        )
            if (q.ExecuteScalar() is not null)
                reasons.Add("Already archived.");
        using (
            var q = Command(
                c,
                tx,
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='deliveries'"
            )
        )
            if (q.ExecuteScalar() is not null)
            {
                using var deliveries = Command(
                    c,
                    tx,
                    "SELECT COUNT(*) FROM deliveries WHERE packet_id=$id AND json_extract(response,'$.status') IN ('queued','processing','retrying')",
                    ("$id", packet.Id)
                );
                if (Convert.ToInt64(deliveries.ExecuteScalar()) > 0)
                    reasons.Add("An in-progress delivery depends on this packet.");
            }
        foreach (var participant in participants)
        {
            if (!ParticipantPresent(c, tx, participant))
                continue;
            try
            {
                reasons.AddRange(participant.Blockers(c, tx, packet));
            }
            catch (SqliteException)
            {
                reasons.Add(
                    "Retention dependency check is unavailable for " + participant.Name + "."
                );
            }
        }
        return new(packet.Id, packet.Revision, reasons.Count == 0, reasons.ToArray());
    }

    public List<OperationsRetentionCandidate> Preview()
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(c, tx, "SELECT json FROM packets");
        using var reader = q.ExecuteReader();
        var packets = new List<Packet>();
        while (reader.Read())
            packets.Add(Decode<Packet>(reader.GetString(0)));
        reader.Close();
        return packets.Select(p => Candidate(c, tx, p, DateTimeOffset.UtcNow)).ToList();
    }

    public OperationsTombstone Archive(
        OperationsRetentionRequest request,
        OperationsIdentity identity
    )
    {
        if (!identity.Administrator)
            throw new OperationsConflict("Administrator access required.", 403);
        if (!RetentionPolicy.ExecutionEnabled || !request.ConfirmArchive)
            throw new OperationsConflict(
                "Retention execution must be enabled and explicitly confirmed."
            );
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(
            c,
            tx,
            "SELECT json FROM packets WHERE id=$id",
            ("$id", request.PacketId)
        );
        var packet = q.ExecuteScalar() is string json
            ? Decode<Packet>(json)
            : throw new OperationsConflict("Packet not found.", 404);
        if (packet.Revision != request.Revision)
            throw new OperationsConflict("Packet revision changed.");
        var now = DateTimeOffset.UtcNow;
        var candidate = Candidate(c, tx, packet, now);
        if (!candidate.Eligible)
            throw new OperationsConflict(string.Join(" ", candidate.Reasons));
        var tombstone = new OperationsTombstone(
            packet.Id,
            packet.Revision,
            packet.EffectiveWorkflow.Id,
            now,
            identity.Name,
            "logical archive; originals and document history retained"
        );
        using var insert = Command(
            c,
            tx,
            "INSERT INTO operations_tombstones VALUES($id,$json)",
            ("$id", packet.Id),
            ("$json", JsonSerializer.Serialize(tombstone, Contract.Json))
        );
        insert.ExecuteNonQuery();
        AuditRetention(c, tx, tombstone);
        tx.Commit();
        return tombstone;
    }

    private OperationsPurgePlan Plan(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        var blockers = Candidate(c, tx, packet, DateTimeOffset.UtcNow)
            .Reasons.Where(r => r != "Already archived.")
            .ToList();
        using (
            var q = Command(
                c,
                tx,
                "SELECT 1 FROM operations_tombstones WHERE packet_id=$id",
                ("$id", packet.Id)
            )
        )
            if (q.ExecuteScalar() is null)
                blockers.Add("Explicit logical archive must precede permanent deletion.");
        using (
            var q = Command(
                c,
                tx,
                "SELECT COUNT(*) FROM revisions WHERE packet_id=$id",
                ("$id", packet.Id)
            )
        )
            if (Convert.ToInt64(q.ExecuteScalar()) > 0)
                blockers.Add("Saved document/review history must be retained.");
        // Unknown tables may contain graph edges even without foreign keys. A module must
        // explicitly own its retention check before it can participate in physical purge.
        var known = new HashSet<string>(
            [
                "packets",
                "revisions",
                "artifacts",
                "intake_keys",
                "queue_work",
                "replayed_letters",
                "workflow_catalog",
                "workflow_settings",
                "operations_tasks",
                "operations_tombstones",
                "operations_retention_audit",
                "continuation_work",
                "wolverine_incoming_envelopes",
                "wolverine_outgoing_envelopes",
                "wolverine_dead_letters",
                "wolverine_nodes",
                "wolverine_node_assignments",
                "wolverine_control_queue",
                "wolverine_dead_letter_history",
                "wolverine_node_records",
                "wolverine_agent_restrictions",
                "wolverine_locks",
                "deliveries",
            ],
            StringComparer.Ordinal
        );
        foreach (var participant in participants)
            known.UnionWith(participant.Tables);
        using (
            var q = Command(
                c,
                tx,
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
            )
        )
        {
            using var reader = q.ExecuteReader();
            while (reader.Read())
                if (!known.Contains(reader.GetString(0)))
                    blockers.Add(
                        "Retention graph is not registered for table " + reader.GetString(0) + "."
                    );
        }
        using (
            var q = Command(
                c,
                tx,
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='deliveries'"
            )
        )
            if (q.ExecuteScalar() is not null)
            {
                using var deliveries = Command(
                    c,
                    tx,
                    "SELECT COUNT(*) FROM deliveries WHERE packet_id=$id",
                    ("$id", packet.Id)
                );
                if (Convert.ToInt64(deliveries.ExecuteScalar()) > 0)
                    blockers.Add("Delivery snapshots/artifacts depend on this packet.");
            }
        if (packet.Id.Length != 32 || packet.Id.Any(ch => !Uri.IsHexDigit(ch)))
            blockers.Add("Packet storage identifier is not safe for filesystem deletion.");
        var directory = Path.Combine(store.Root, packet.Id);
        if (Directory.Exists(directory) && HasReparsePoint(directory))
            blockers.Add("Packet storage contains a filesystem link.");
        return new(packet.Id, blockers.Count == 0, blockers.ToArray());
    }

    private static bool HasReparsePoint(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            if ((attributes & FileAttributes.Directory) != 0 && HasReparsePoint(entry))
                return true;
        }
        return false;
    }

    private static bool ParticipantPresent(
        SqliteConnection c,
        SqliteTransaction tx,
        IOperationsRetentionParticipant participant
    )
    {
        if (participant.Tables.Length == 0)
            return true;
        foreach (var table in participant.Tables)
        {
            using var q = Command(
                c,
                tx,
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name",
                ("$name", table)
            );
            if (q.ExecuteScalar() is not null)
                return true;
        }
        return false;
    }

    private static bool TableExists(SqliteConnection c, SqliteTransaction tx, string name)
    {
        using var q = Command(
            c,
            tx,
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name",
            ("$name", name)
        );
        return q.ExecuteScalar() is not null;
    }

    private static bool HasDurableDependency(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet
    )
    {
        if (TableExists(c, tx, "continuation_work"))
        {
            using var pending = Command(
                c,
                tx,
                "SELECT 1 FROM continuation_work WHERE packet_id=$id AND revision=$revision AND state IN ('queued','running','failed')",
                ("$id", packet.Id),
                ("$revision", packet.Revision)
            );
            if (pending.ExecuteScalar() is not null)
                return true;
        }
        using var generations = Command(
            c,
            tx,
            "SELECT kind,work_id FROM queue_work WHERE id=$id",
            ("$id", packet.Id)
        );
        using var reader = generations.ExecuteReader();
        var work = new List<(string Kind, string WorkId)>();
        while (reader.Read())
            work.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();
        foreach (var (kind, workId) in work)
        {
            if (kind is not ("packet" or "continuation"))
                return true;
            // Match the exact random generation token in Wolverine's serialized body;
            // completed dedup receipts and superseded generations are not pending work.
            if (TableExists(c, tx, "wolverine_incoming_envelopes"))
            {
                using var inbox = Command(
                    c,
                    tx,
                    "SELECT 1 FROM wolverine_incoming_envelopes WHERE status!='Handled' AND instr(body,CAST($work AS BLOB))>0 LIMIT 1",
                    ("$work", workId)
                );
                if (inbox.ExecuteScalar() is not null)
                    return true;
            }
            if (TableExists(c, tx, "wolverine_dead_letters"))
            {
                using var dead = Command(
                    c,
                    tx,
                    "SELECT 1 FROM wolverine_dead_letters WHERE archived_at IS NULL AND instr(body,CAST($work AS BLOB))>0 LIMIT 1",
                    ("$work", workId)
                );
                if (dead.ExecuteScalar() is not null)
                    return true;
            }
        }
        return false;
    }

    public OperationsPurgePlan PurgePlan(string id)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(c, tx, "SELECT json FROM packets WHERE id=$id", ("$id", id));
        var packet = q.ExecuteScalar() is string json
            ? Decode<Packet>(json)
            : throw new OperationsConflict("Packet not found.", 404);
        return Plan(c, tx, packet);
    }

    public OperationsTombstone Purge(OperationsPurgeRequest request, OperationsIdentity identity)
    {
        if (!identity.Administrator)
            throw new OperationsConflict("Administrator access required.", 403);
        if (
            !RetentionPolicy.ExecutionEnabled
            || !config.GetValue<bool>("Paperwork:Operations:Retention:PermanentDeletionEnabled")
            || !request.ConfirmPermanentDeletion
        )
            throw new OperationsConflict(
                "Permanent deletion must be separately enabled and explicitly confirmed."
            );
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = Command(
            c,
            tx,
            "SELECT json FROM packets WHERE id=$id",
            ("$id", request.PacketId)
        );
        var packet = q.ExecuteScalar() is string json
            ? Decode<Packet>(json)
            : throw new OperationsConflict("Packet not found.", 404);
        if (packet.Revision != request.Revision)
            throw new OperationsConflict("Packet revision changed.");
        var plan = Plan(c, tx, packet);
        if (!plan.Eligible)
            throw new OperationsConflict(string.Join(" ", plan.Blockers));
        // Delete only originals in an already archived, dependency-free packet. The DB
        // tombstone is durable before deletion starts; interrupted work is retryable.
        var directory = Path.Combine(store.Root, packet.Id);
        var started = new OperationsTombstone(
            packet.Id,
            packet.Revision,
            packet.EffectiveWorkflow.Id,
            DateTimeOffset.UtcNow,
            identity.Name,
            "permanent deletion requested"
        );
        using (
            var update = Command(
                c,
                tx,
                "UPDATE operations_tombstones SET json=$json WHERE packet_id=$id",
                ("$json", JsonSerializer.Serialize(started, Contract.Json)),
                ("$id", packet.Id)
            )
        )
            update.ExecuteNonQuery();
        AuditRetention(c, tx, started);
        tx.Commit();
        // Re-open a write transaction and recheck all dependencies immediately before deleting.
        using var deletion = c.BeginTransaction(deferred: false);
        using var current = Command(
            c,
            deletion,
            "SELECT json FROM packets WHERE id=$id",
            ("$id", packet.Id)
        );
        var latest = current.ExecuteScalar() is string currentJson
            ? Decode<Packet>(currentJson)
            : null;
        if (latest is null || latest.Revision != request.Revision)
            throw new OperationsConflict("Packet changed before deletion.");
        plan = Plan(c, deletion, latest);
        if (!plan.Eligible)
            throw new OperationsConflict(string.Join(" ", plan.Blockers));
        foreach (var participant in participants)
            if (ParticipantPresent(c, deletion, participant))
                participant.Purge(c, deletion, latest);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        foreach (var table in new[] { "artifacts", "intake_keys" })
        {
            using var delete = Command(
                c,
                deletion,
                $"DELETE FROM {table} WHERE packet_id=$id",
                ("$id", packet.Id)
            );
            delete.ExecuteNonQuery();
        }
        using (
            var delete = Command(
                c,
                deletion,
                "DELETE FROM packets WHERE id=$id",
                ("$id", packet.Id)
            )
        )
            delete.ExecuteNonQuery();
        using (
            var generations = Command(
                c,
                deletion,
                "DELETE FROM queue_work WHERE id=$id AND kind IN ('packet','continuation')",
                ("$id", packet.Id)
            )
        )
            generations.ExecuteNonQuery();
        if (TableExists(c, deletion, "continuation_work"))
        {
            using var continuation = Command(
                c,
                deletion,
                "DELETE FROM continuation_work WHERE packet_id=$id",
                ("$id", packet.Id)
            );
            continuation.ExecuteNonQuery();
        }
        var complete = started with
        {
            At = DateTimeOffset.UtcNow,
            Action = "permanently deleted; audit tombstone retained",
        };
        using (
            var update = Command(
                c,
                deletion,
                "UPDATE operations_tombstones SET json=$json WHERE packet_id=$id",
                ("$json", JsonSerializer.Serialize(complete, Contract.Json)),
                ("$id", packet.Id)
            )
        )
            update.ExecuteNonQuery();
        AuditRetention(c, deletion, complete);
        deletion.Commit();
        return complete;
    }
}
