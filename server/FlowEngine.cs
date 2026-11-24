using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class FlowEngine
{
    private readonly Store store;
    private readonly OperationsPlatformStore? operations;

    private static string RunKey(Packet packet) =>
        $"{packet.EffectiveWorkflow.Hash}:revision:{packet.Revision}";

    public FlowEngine(Store store, OperationsPlatformStore? operations = null)
    {
        this.store = store;
        this.operations = operations;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS flow_runs(packet_id TEXT NOT NULL,snapshot_hash TEXT NOT NULL,definition_hash TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,snapshot_hash)); CREATE TABLE IF NOT EXISTS flow_blocks(packet_id TEXT NOT NULL,snapshot_hash TEXT NOT NULL,block_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,snapshot_hash,block_id));";
        q.ExecuteNonQuery();
    }

    public FlowRun? Get(Packet packet)
    {
        using var c = store.Open();
        return Read(c, null, packet);
    }

    private static FlowRun? Read(SqliteConnection c, SqliteTransaction? tx, Packet packet)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM flow_runs WHERE packet_id=$p AND snapshot_hash=$h";
        q.Parameters.AddWithValue("$p", packet.Id);
        q.Parameters.AddWithValue("$h", RunKey(packet));
        if (q.ExecuteScalar() is not string json)
            return null;
        var run = JsonSerializer.Deserialize<FlowRun>(json, Contract.Json)!;
        q.CommandText =
            "SELECT json FROM flow_blocks WHERE packet_id=$p AND snapshot_hash=$h ORDER BY rowid";
        using var r = q.ExecuteReader();
        var executions = new List<FlowExecution>();
        while (r.Read())
            executions.Add(
                JsonSerializer.Deserialize<FlowExecution>(r.GetString(0), Contract.Json)!
            );
        return run with { Executions = executions.ToArray() };
    }

    public Task<FlowRun?> ExecuteAsync(
        Packet packet,
        IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler> handlers,
        CancellationToken ct
    ) => ExecuteAsync(packet, packet.EffectiveWorkflow.Capabilities?.Flow, handlers, ct);

    public Task<FlowRun?> ExecuteAsync(
        Packet packet,
        FlowPolicy? policy,
        IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler> handlers,
        CancellationToken ct
    ) => ExecuteAsync(packet, policy, handlers, ct, null);

    internal Task<FlowRun?> ExecuteAsync(
        Packet packet,
        IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler> handlers,
        Func<Packet, CancellationToken, Task> prepareExtraction,
        CancellationToken ct
    ) =>
        ExecuteAsync(
            packet,
            packet.EffectiveWorkflow.Capabilities?.Flow,
            handlers,
            ct,
            prepareExtraction
        );

    private async Task<FlowRun?> ExecuteAsync(
        Packet packet,
        FlowPolicy? policy,
        IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler> handlers,
        CancellationToken ct,
        Func<Packet, CancellationToken, Task>? prepareExtraction
    )
    {
        if (policy is null)
            return null;
        // Clone at the boundary so mutable arrays supplied by callers cannot change an active run.
        var definition = JsonSerializer.Deserialize<FlowDefinition>(
            JsonSerializer.Serialize(policy.Definition, Contract.Json),
            Contract.Json
        )!;
        FlowValidator.Validate(definition);
        foreach (var block in definition.Blocks)
            if (
                block.Kind is not (FlowBlockKind.Transform or FlowBlockKind.Validate)
                && !handlers.ContainsKey(block.Kind)
            )
                throw new WorkflowException(400, $"No registered handler for {block.Kind}.");
        var hash = Convert
            .ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition, Contract.Json))
            )
            .ToLowerInvariant();
        using (var c = store.Open())
        using (var tx = c.BeginTransaction())
        {
            var existing = Read(c, tx, packet);
            if (existing is not null && existing.DefinitionHash != hash)
                throw new WorkflowException(
                    409,
                    "An accepted snapshot cannot change its flow definition."
                );
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText =
                "SELECT 1 FROM flow_runs WHERE packet_id=$p AND json_extract(json,'$.snapshotHash')=$s AND definition_hash != $d LIMIT 1";
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$s", packet.EffectiveWorkflow.Hash);
            q.Parameters.AddWithValue("$d", hash);
            if (q.ExecuteScalar() is not null)
                throw new WorkflowException(
                    409,
                    "An accepted snapshot cannot change its flow definition across revisions."
                );
            q.Parameters.Clear();
            q.CommandText = "INSERT OR IGNORE INTO flow_runs VALUES($p,$h,$d,$j)";
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$h", RunKey(packet));
            q.Parameters.AddWithValue("$d", hash);
            q.Parameters.AddWithValue(
                "$j",
                JsonSerializer.Serialize(
                    new FlowRun(
                        packet.Id,
                        packet.EffectiveWorkflow.Hash,
                        hash,
                        definition,
                        [],
                        packet.Revision
                    ),
                    Contract.Json
                )
            );
            q.ExecuteNonQuery();
            tx.Commit();
        }
        foreach (var original in definition.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            var authoritative = store.Get(packet.Id)
                ?? throw new WorkflowException(404, "Packet not found.");
            if (
                authoritative.Revision != packet.Revision
                || authoritative.EffectiveWorkflow.Hash != packet.EffectiveWorkflow.Hash
                || authoritative.Status == "cancelled"
            )
                throw new WorkflowException(
                    409,
                    "Packet changed while the flow was running. Resume its current revision."
                );
            var run = Get(packet)!;
            var prior = run.Executions.SingleOrDefault(e => e.BlockId == original.Id);
            if (prior?.Status is "completed" or "skipped")
                continue;
            if (prior?.Status == "waitingApproval" && packet.Status != "approved")
                return run;
            if (prior?.Status is "failed" or "running")
                throw new WorkflowException(409, "Flow needs an explicit retry before resuming.");
            var ancestors = Ancestors(definition, original);
            var dependencies = run
                .Executions.Where(e => original.DependsOn.Contains(e.BlockId))
                .ToArray();
            if (
                dependencies.Length != original.DependsOn.Length
                || dependencies.Any(e => e.Status is not ("completed" or "skipped"))
            )
                throw new WorkflowException(409, "Flow dependencies are incomplete.");
            var fields = new Dictionary<string, string>();
            var verified = new HashSet<string>();
            foreach (
                var execution in run.Executions.Where(e =>
                    ancestors.Contains(e.BlockId) && e.Status == "completed"
                )
            )
            {
                foreach (var field in execution.Result?.Fields ?? [])
                    fields[field.Key] = field.Value;
                foreach (var reference in execution.Result?.VerifiedReferences ?? [])
                    verified.Add(reference);
            }
            bool allowed =
                original.When is null || Matches(original.When, packet, fields, verified);
            // Unconditional descendants of a skipped branch remain skipped; conditional join blocks can explicitly route independently.
            if (original.When is null && dependencies.Any(e => e.Status == "skipped"))
                allowed = false;
            var key = $"flow:{packet.Id}:{RunKey(packet)}:{original.Id}";
            var executionRecord = new FlowExecution(
                original.Id,
                allowed ? "running" : "skipped",
                (prior?.Attempt ?? 0) + 1,
                key,
                DateTimeOffset.UtcNow,
                null,
                null
            );
            Claim(packet, executionRecord);
            if (!allowed)
                continue;
            var block = original.TemplateId is null
                ? original
                : original with
                {
                    Operations = definition
                        .Templates!.Single(t => t.Id == original.TemplateId)
                        .Operations,
                };
            bool preparingExtraction = false;
            try
            {
                if (block.Kind == FlowBlockKind.Output && packet.Status != "approved")
                    throw new FlowAwaitingApprovalException();
                if (block.Kind == FlowBlockKind.Extraction && prepareExtraction is not null)
                {
                    preparingExtraction = true;
                    await prepareExtraction(packet, ct);
                    preparingExtraction = false;
                }
                var result = block.Kind is FlowBlockKind.Transform or FlowBlockKind.Validate
                    ? Safe(block, fields)
                    : await handlers[block.Kind](new(packet, block, key, fields, verified), ct);
                if (
                    result is null
                    || result.Fields is null
                    || result.VerifiedReferences is null
                    || result.Fields.Count > 256
                    || result.Fields.Any(f =>
                        f.Key.Length > 100 || f.Value is null || f.Value.Length > 4000
                    )
                    || result.VerifiedReferences.Length > 256
                    || result.VerifiedReferences.Any(r => r is null || r.Length > 500)
                )
                    throw new InvalidOperationException(
                        "Handler returned an invalid bounded result."
                    );
                Finish(
                    packet,
                    executionRecord with
                    {
                        Status = "completed",
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Result = result,
                    }
                );
            }
            catch (FlowAwaitingApprovalException)
            {
                Finish(
                    packet,
                    executionRecord with
                    {
                        Status = "waitingApproval",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }
                );
                return Get(packet);
            }
            catch (Exception ex)
            {
                // Only the cache-backed AI preparation is safe to return to the durable queue
                // automatically. Leave other handler failures subject to explicit recovery.
                bool resumable =
                    preparingExtraction
                    && (
                        (ex is OperationCanceledException && ct.IsCancellationRequested)
                        || Messaging.SafeFailure(ex) is TransientWorkException
                    );
                Finish(
                    packet,
                    executionRecord with
                    {
                        Status = resumable ? "pending" : "failed",
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Error = resumable
                            ? null
                            : "Block failed. Review its service diagnostics, then retry explicitly.",
                    }
                );
                throw;
            }
        }
        return Get(packet);
    }

    private static HashSet<string> Ancestors(FlowDefinition definition, FlowBlock block)
    {
        var found = new HashSet<string>();
        void Visit(string id)
        {
            if (found.Add(id))
                foreach (var dependency in definition.Blocks.Single(b => b.Id == id).DependsOn)
                    Visit(dependency);
        }
        foreach (var dependency in block.DependsOn)
            Visit(dependency);
        return found;
    }

    private static bool Matches(
        FlowCondition condition,
        Packet packet,
        Dictionary<string, string> fields,
        HashSet<string> verified
    )
    {
        bool match = condition.Kind switch
        {
            FlowConditionKind.DocumentType => packet.Documents.Any(d => d.Type == condition.Key),
            FlowConditionKind.FieldEquals => fields.TryGetValue(condition.Key, out var value)
                && value == condition.Value,
            FlowConditionKind.ReferenceVerified => verified.Contains(condition.Key),
            _ => false,
        };
        return condition.Negate ? !match : match;
    }

    private static FlowBlockResult Safe(FlowBlock block, Dictionary<string, string> fields)
    {
        var output = new Dictionary<string, string>();
        foreach (var operation in block.Operations!)
        {
            var value = output.GetValueOrDefault(operation.Source)
                ?? fields.GetValueOrDefault(operation.Source)
                ?? "";
            output[operation.Target] = operation.Kind switch
            {
                FlowTransformKind.Trim => value.Trim(),
                FlowTransformKind.Uppercase => value.ToUpperInvariant(),
                FlowTransformKind.Lowercase => value.ToLowerInvariant(),
                FlowTransformKind.RequireValue when !string.IsNullOrWhiteSpace(value) => value,
                _ => throw new WorkflowException(422, "A required flow field is missing."),
            };
        }
        return new(output, []);
    }

    private void Claim(Packet packet, FlowExecution execution)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var prior = Read(c, tx, packet)!
            .Executions.SingleOrDefault(e => e.BlockId == execution.BlockId);
        if (
            execution.Attempt != (prior?.Attempt ?? 0) + 1
            || (prior is not null && prior.Status is not ("pending" or "waitingApproval"))
        )
            throw new WorkflowException(
                409,
                "Another executor claimed this block. Reload progress."
            );
        Save(c, tx, packet, execution);
        tx.Commit();
    }

    private void Finish(Packet packet, FlowExecution execution)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var prior = Read(c, tx, packet)!.Executions.Single(e => e.BlockId == execution.BlockId);
        if (prior.Attempt != execution.Attempt || prior.Status != "running")
            throw new WorkflowException(409, "Execution ownership changed.");
        Save(c, tx, packet, execution);
        tx.Commit();
    }

    public FlowRun Retry(Packet packet, FlowRetryRequest request, string actor = "system")
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var current = Store.Read(c, tx, packet.Id)
            ?? throw new WorkflowException(404, "Packet not found.");
        if (current.Revision != request.Revision)
            throw new WorkflowException(409, "Packet revision changed.");
        if (operations?.ValidateMutation(c, tx, current, actor) is { } claimError)
            throw new WorkflowException(409, claimError);
        var run = Read(c, tx, current) ?? throw new WorkflowException(404, "Flow run not found.");
        var execution = run.Executions.SingleOrDefault(e => e.BlockId == request.BlockId)
            ?? throw new WorkflowException(404, "Block not found.");
        if (
            execution.Status != "failed"
            && !(
                execution.Status == "running"
                && execution.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-15)
            )
        )
            throw new WorkflowException(
                409,
                "Only failed or abandoned blocks can be retried. Running blocks require a 15-minute recovery window."
            );
        Save(
            c,
            tx,
            current,
            execution with
            {
                Status = "pending",
                Error = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        if (current.Status is not ("failed" or "processing" or "approved"))
            throw new WorkflowException(
                409,
                "Only failed, abandoned processing or approved packets can resume a flow."
            );
        var approved = current.Status == "approved";
        if (!approved)
            current.Status = "queued";
        current.Error = null;
        current.Audit.Add(
            new(DateTimeOffset.UtcNow, "flow block retry requested", current.Revision, actor)
        );
        Store.Write(c, tx, current);
        if (approved)
            store.Commit(tx, new ContinuePlatformFlow(current.Id, current.Revision));
        else
            store.Commit(tx, new ProcessPacket(current.Id));
        return Get(current)!;
    }

    private static void Save(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        FlowExecution execution
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT INTO flow_blocks VALUES($p,$h,$b,$j) ON CONFLICT(packet_id,snapshot_hash,block_id) DO UPDATE SET json=excluded.json";
        q.Parameters.AddWithValue("$p", packet.Id);
        q.Parameters.AddWithValue("$h", RunKey(packet));
        q.Parameters.AddWithValue("$b", execution.BlockId);
        q.Parameters.AddWithValue("$j", JsonSerializer.Serialize(execution, Contract.Json));
        q.ExecuteNonQuery();
    }
}
