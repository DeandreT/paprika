using JasperFx;
using Microsoft.Data.Sqlite;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Sqlite;

namespace paprika;

// These persisted identities predate the rename and must remain readable after an upgrade.
[Wolverine.Attributes.MessageIdentity("PaperworkClassifier.ProcessPacket")]
public sealed record ProcessPacket(string PacketId, string? WorkId = null);

[Wolverine.Attributes.MessageIdentity("PaperworkClassifier.ProcessDelivery")]
public sealed record ProcessDelivery(string DeliveryId, string? WorkId = null);

public sealed class TransientWorkException()
    : Exception("A transient local or provider operation failed.");

public sealed class PermanentWorkException()
    : Exception("The operation could not be completed. Check configuration and source files.");

public sealed class ProcessPacketHandler
{
    public static async Task Handle(
        ProcessPacket command,
        PacketProcessor processor,
        Store store,
        Envelope envelope,
        CancellationToken ct
    )
    {
        Messaging.ThrowIfFinalizingFault(envelope);
        if (!DurableQueue.CanProcess(store, "packet", command.PacketId, command.WorkId))
            return;
        try
        {
            await processor.Process(command.PacketId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Messaging.SafeFailure(ex);
        }
    }
}

public sealed class ProcessDeliveryHandler
{
    public static void Handle(
        ProcessDelivery command,
        DeliveryStore deliveries,
        Store store,
        Envelope envelope,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Messaging.ThrowIfFinalizingFault(envelope);
        if (!DurableQueue.CanProcess(store, "delivery", command.DeliveryId, command.WorkId))
            return;
        try
        {
            deliveries.Process(command.DeliveryId);
        }
        catch (Exception ex)
        {
            throw Messaging.SafeFailure(ex);
        }
    }
}

// The existing ADO transaction owns BOTH the business row and Wolverine's durable inbox row.
// Flush only follows commit; recovery owns a committed row if the process dies before flush.
public sealed class DurableQueue(IServiceProvider services)
{
    public void Commit(SqliteTransaction tx, object message) =>
        CommitAsync(tx, [message]).GetAwaiter().GetResult();

    internal async Task CommitAsync(SqliteTransaction tx, IEnumerable<object> messages)
    {
        var runtime = services.GetRequiredService<IWolverineRuntime>();
        var context = new MessageContext(runtime);
        await context.EnlistInOutboxAsync(
            new DatabaseEnvelopeTransaction((IMessageDatabase)context.Storage, tx)
        );
        foreach (var original in messages)
        {
            var workId = Guid.NewGuid().ToString("N");
            var (kind, id, message) = original switch
            {
                ProcessPacket p => ("packet", p.PacketId, (object)(p with { WorkId = workId })),
                ProcessDelivery d => (
                    "delivery",
                    d.DeliveryId,
                    (object)(d with { WorkId = workId })
                ),
                ContinuePlatformFlow flow => (
                    "continuation",
                    flow.PacketId,
                    (object)(flow with { WorkId = workId })
                ),
                _ => throw new ArgumentException("Unsupported durable command."),
            };
            using var q = tx.Connection!.CreateCommand();
            q.Transaction = tx;
            q.CommandText =
                "INSERT INTO queue_work VALUES($kind,$id,$work) ON CONFLICT(kind,id) DO UPDATE SET work_id=$work";
            q.Parameters.AddWithValue("$kind", kind);
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$work", workId);
            q.ExecuteNonQuery();
            if (message is ContinuePlatformFlow continuation)
                ContinuationWork.Queue(tx.Connection!, tx, continuation);
            await context.SendAsync(message);
        }
        await tx.CommitAsync();
        // Do not report a committed acceptance as failed if immediate dispatch is unavailable.
        // Wolverine's persisted inbox recovery will dispatch the row after restart.
        try
        {
            await context.FlushOutgoingMessagesAsync();
        }
        catch (Exception)
        {
            services
                .GetRequiredService<ILogger<DurableQueue>>()
                .LogWarning("Durable work committed; dispatch deferred to inbox recovery.");
        }
    }

    internal static bool CanProcess(Store store, string kind, string id, string? workId)
    {
        if (workId is null)
            return true; // Explicit direct invocations used in focused business tests.
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        if (Current(c, tx, kind, id) != workId)
            return false;
        var status =
            kind == "packet"
                ? Store.Read(c, tx, id)?.Status
                : DeliveryStore.Read(c, tx, "WHERE id=$id", ("$id", id))?.Status;
        if (status == "failed")
            throw new PermanentWorkException();
        return status is "queued" or "processing";
    }

    internal static string? Current(
        SqliteConnection c,
        SqliteTransaction tx,
        string kind,
        string id
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT work_id FROM queue_work WHERE kind=$kind AND id=$id";
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() as string;
    }
}

public static class ContinuationWork
{
    internal static void Initialize(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS continuation_work(packet_id TEXT PRIMARY KEY,revision INTEGER NOT NULL,work_id TEXT NOT NULL,state TEXT NOT NULL,error TEXT);";
        q.ExecuteNonQuery();
    }

    internal static void Queue(
        SqliteConnection c,
        SqliteTransaction tx,
        ContinuePlatformFlow command
    )
    {
        Initialize(c, tx);
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT INTO continuation_work VALUES($id,$revision,$work,'queued',NULL) ON CONFLICT(packet_id) DO UPDATE SET revision=$revision,work_id=$work,state='queued',error=NULL";
        q.Parameters.AddWithValue("$id", command.PacketId);
        q.Parameters.AddWithValue("$revision", command.Revision);
        q.Parameters.AddWithValue("$work", command.WorkId!);
        q.ExecuteNonQuery();
    }

    public static bool CanProcess(
        Store store,
        ContinuePlatformFlow command,
        bool finalizing = false
    )
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var packet = Store.Read(c, tx, command.PacketId);
        if (packet?.Status != "approved" || packet.Revision != command.Revision)
            return false;
        if (command.WorkId is null)
            return true;
        if (DurableQueue.Current(c, tx, "continuation", command.PacketId) != command.WorkId)
            return false;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT state FROM continuation_work WHERE packet_id=$id AND revision=$revision AND work_id=$work";
        q.Parameters.AddWithValue("$id", command.PacketId);
        q.Parameters.AddWithValue("$revision", command.Revision);
        q.Parameters.AddWithValue("$work", command.WorkId);
        var state = q.ExecuteScalar() as string;
        if (state == "failed")
        {
            if (finalizing)
                return true;
            throw new PermanentWorkException();
        }
        return state == "queued";
    }

    public static void Complete(Store store, ContinuePlatformFlow command)
    {
        if (command.WorkId is null)
            return;
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "UPDATE continuation_work SET state='completed',error=NULL WHERE packet_id=$id AND revision=$revision AND work_id=$work AND state='queued'";
        q.Parameters.AddWithValue("$id", command.PacketId);
        q.Parameters.AddWithValue("$revision", command.Revision);
        q.Parameters.AddWithValue("$work", command.WorkId);
        q.ExecuteNonQuery();
        tx.Commit();
    }

    internal static bool Failed(
        SqliteConnection c,
        SqliteTransaction tx,
        ContinuePlatformFlow command
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT 1 FROM continuation_work WHERE packet_id=$id AND revision=$revision AND work_id=$work AND state='failed'";
        q.Parameters.AddWithValue("$id", command.PacketId);
        q.Parameters.AddWithValue("$revision", command.Revision);
        q.Parameters.AddWithValue("$work", command.WorkId ?? "");
        return q.ExecuteScalar() is not null;
    }
}

// Runs once per host start, not a business polling loop. A generation row is committed with each
// recovered legacy command, so subsequent starts cannot resurrect exhausted or completed work.
public sealed class MessagingStartup(Store store, DeliveryStore deliveries, DurableQueue queue)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        _ = deliveries;
        store.EnableMessaging(queue);
        using var c = store.Open();
        ContinuationWork.Initialize(c);
        using var tx = c.BeginTransaction();
        var commands = new List<object>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT id FROM packets WHERE json_extract(json,'$.status') IN ('queued','processing') AND NOT EXISTS (SELECT 1 FROM queue_work w WHERE w.kind='packet' AND w.id=packets.id)";
            using var r = q.ExecuteReader();
            while (r.Read())
                commands.Add(new ProcessPacket(r.GetString(0)));
        }
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText =
                "SELECT id FROM deliveries WHERE json_extract(response,'$.status') IN ('queued','processing') AND NOT EXISTS (SELECT 1 FROM queue_work w WHERE w.kind='delivery' AND w.id=deliveries.id)";
            using var r = q.ExecuteReader();
            while (r.Read())
                commands.Add(new ProcessDelivery(r.GetString(0)));
        }
        await queue.CommitAsync(tx, commands);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class MessagingLease(IConfiguration configuration) : IHostedService, IDisposable
{
    private FileStream? lease;

    public Task StartAsync(CancellationToken ct)
    {
        var root = Messaging.DataRoot(configuration);
        Directory.CreateDirectory(root);
        lease = new FileStream(
            Path.Combine(root, "messaging.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lease?.Dispose();
        lease = null;
    }
}

public sealed class WorkFailureRecorder(Store store)
{
    public void Record(Envelope envelope, Exception exception)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        envelope.Headers[Messaging.FaultHeader] =
            exception is TransientWorkException ? "transient" : "permanent";
        using (var checkpoint = c.CreateCommand())
        {
            checkpoint.Transaction = tx;
            checkpoint.CommandText =
                "UPDATE wolverine_incoming_envelopes SET body=$body WHERE id=$id AND received_at=$address";
            checkpoint.Parameters.AddWithValue("$body", EnvelopeSerializer.Serialize(envelope));
            checkpoint.Parameters.AddWithValue("$id", envelope.Id);
            checkpoint.Parameters.AddWithValue("$address", envelope.Destination!.ToString());
            checkpoint.ExecuteNonQuery();
        }
        if (
            envelope.Message is ProcessPacket p
            && DurableQueue.Current(c, tx, "packet", p.PacketId) == p.WorkId
        )
        {
            var packet = Store.Read(c, tx, p.PacketId);
            if (packet?.Status is "queued" or "processing")
            {
                packet.Status = "failed";
                packet.Error =
                    "Processing failed. Inspect Messaging and correct the cause before retrying.";
                packet.Processing = new("failed", packet.Processing?.Model);
                packet.Audit.Add(
                    new(
                        DateTimeOffset.UtcNow,
                        "processing failed; durable dead letter",
                        packet.Revision
                    )
                );
                Store.Write(c, tx, packet);
            }
        }
        if (
            envelope.Message is ProcessDelivery d
            && DurableQueue.Current(c, tx, "delivery", d.DeliveryId) == d.WorkId
        )
        {
            var delivery = DeliveryStore.Read(c, tx, "WHERE id=$id", ("$id", d.DeliveryId));
            if (delivery?.Status is "queued" or "processing")
                DeliveryStore.Save(
                    c,
                    tx,
                    delivery with
                    {
                        Status = "failed",
                        Error =
                            "Delivery failed. Inspect Messaging and correct the cause before retrying.",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }
                );
        }
        if (
            envelope.Message is ContinuePlatformFlow flow
            && flow.WorkId is not null
            && DurableQueue.Current(c, tx, "continuation", flow.PacketId) == flow.WorkId
        )
        {
            var packet = Store.Read(c, tx, flow.PacketId);
            if (packet?.Status == "approved" && packet.Revision == flow.Revision)
            {
                using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText =
                    "UPDATE continuation_work SET state='failed',error='Approved continuation failed. Inspect Messaging and correct the cause before replaying.' WHERE packet_id=$id AND revision=$revision AND work_id=$work AND state!='completed'";
                q.Parameters.AddWithValue("$id", flow.PacketId);
                q.Parameters.AddWithValue("$revision", flow.Revision);
                q.Parameters.AddWithValue("$work", flow.WorkId);
                if (q.ExecuteNonQuery() > 0)
                {
                    if (
                        !packet.Audit.Any(a =>
                            a.Action == "approved continuation failed; durable dead letter"
                            && a.Revision == flow.Revision
                        )
                    )
                        packet.Audit.Add(
                            new(
                                DateTimeOffset.UtcNow,
                                "approved continuation failed; durable dead letter",
                                packet.Revision
                            )
                        );
                    Store.Write(c, tx, packet);
                }
            }
        }
        tx.Commit();
    }

    public async Task PersistDeadLetter(
        IWolverineRuntime runtime,
        Envelope envelope,
        Exception exception
    )
    {
        var database = (IMessageDatabase)runtime.Storage;
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var exists = c.CreateCommand();
        exists.Transaction = tx;
        exists.CommandText = "SELECT 1 FROM wolverine_dead_letters WHERE id=$id";
        exists.Parameters.AddWithValue("$id", envelope.Id);
        if (exists.ExecuteScalar() is null)
        {
            // Wolverine 6.64's generic SQLite move executes its delete and insert as an
            // untransactional batch. Use its public persistence command builder inside OUR
            // transaction so a failed fault write cannot delete the only recoverable inbox row.
            var builder = database.ToCommandBuilder();
            DatabasePersistence.ConfigureDeadLetterCommands(
                runtime.Options.Durability,
                envelope,
                exception,
                builder,
                database
            );
            await using var insert = builder.Compile();
            insert.Connection = c;
            insert.Transaction = tx;
            await insert.ExecuteNonQueryAsync();
        }
        using var remove = c.CreateCommand();
        remove.Transaction = tx;
        remove.CommandText =
            "DELETE FROM wolverine_incoming_envelopes WHERE id=$id AND received_at=$address";
        remove.Parameters.AddWithValue("$id", envelope.Id);
        remove.Parameters.AddWithValue("$address", envelope.Destination!.ToString());
        remove.ExecuteNonQuery();
        tx.Commit();
    }
}

public static class Messaging
{
    internal const string FaultHeader = "paperwork-fault-finalization";

    internal static void ThrowIfFinalizingFault(Envelope envelope)
    {
        if (envelope.Headers.TryGetValue(FaultHeader, out var reason))
            throw reason == "transient"
                ? new TransientWorkException()
                : new PermanentWorkException();
    }

    internal static string DataRoot(IConfiguration configuration) =>
        AppPaths.DataRoot(configuration);

    public sealed class LocalConnection(IConfiguration configuration) : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            var root = DataRoot(configuration);
            Directory.CreateDirectory(root);
            configuration["ConnectionStrings:paprikaDurability"] = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "jobs.sqlite"),
            }.ToString();
        }
    }

    internal static Exception SafeFailure(Exception ex) =>
        ex is IOException or TimeoutException or TaskCanceledException
        || ex
            is HttpRequestException
            {
                StatusCode: null
                    or System.Net.HttpStatusCode.TooManyRequests
                    or System.Net.HttpStatusCode.RequestTimeout
            }
        || ex is HttpRequestException http && (int?)http.StatusCode >= 500
            ? new TransientWorkException()
            : new PermanentWorkException();

    private static async ValueTask Fail(
        IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception
    )
    {
        var recorder = runtime.Services.GetRequiredService<WorkFailureRecorder>();
        try
        {
            recorder.Record(lifecycle.Envelope!, exception);
            await recorder.PersistDeadLetter(runtime, lifecycle.Envelope!, exception);
        }
        catch (Exception)
        {
            runtime
                .Services.GetRequiredService<ILogger<WorkFailureRecorder>>()
                .LogWarning(
                    "Fault persistence unavailable; original durable envelope retained for fault finalization."
                );
            await lifecycle.ReScheduleAsync(DateTimeOffset.UtcNow.AddSeconds(3));
            return;
        }
        await lifecycle.CompleteAsync();
    }

    public static IHostBuilder UsePacketMessaging(this IHostBuilder builder)
    {
        // Resolve exactly the same file as the application store. No rebuild, purge, or destructive resource setup.
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IHostedService, MessagingLease>();
            services.AddSingleton<IWolverineExtension, LocalConnection>();
        });
        builder.UseWolverine(options =>
        {
            options.ApplicationAssembly = typeof(ProcessPacket).Assembly;
            options.Durability.Mode = DurabilityMode.Solo;
            options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
            options
                .PersistMessagesWithSqliteUsingNamedConnection("paprikaDurability")
                .OverrideAutoCreateResources(AutoCreate.CreateOrUpdate);
            options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;
            options
                .PublishMessage<ProcessPacket>()
                .ToLocalQueue("processing")
                .UseDurableInbox()
                .MaximumParallelMessages(1);
            options
                .PublishMessage<ProcessDelivery>()
                .ToLocalQueue("delivery")
                .UseDurableInbox()
                .MaximumParallelMessages(1);
            options
                .PublishMessage<ContinuePlatformFlow>()
                .ToLocalQueue("continuation")
                .UseDurableInbox()
                .MaximumParallelMessages(1);
            options
                .Policies.OnException<TransientWorkException>()
                .ScheduleRetry(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(10)
                )
                .Then.CustomActionIndefinitely(
                    Fail,
                    "Record terminal business failure and persist Wolverine dead letter"
                );
            options
                .Policies.OnException<PermanentWorkException>()
                .CustomActionIndefinitely(
                    Fail,
                    "Record terminal business failure and persist Wolverine dead letter"
                );
            options
                .Discovery.DisableConventionalDiscovery()
                .IncludeType<ProcessPacketHandler>()
                .IncludeType<ProcessDeliveryHandler>()
                .IncludeType<ContinuePlatformFlowHandler>();
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<PacketProcessor>();
            services.AddSingleton<DurableQueue>();
            services.AddSingleton<WorkFailureRecorder>();
            services.AddSingleton<MessagingOperations>();
            services.AddHostedService<MessagingStartup>();
        });
        return builder;
    }
}
