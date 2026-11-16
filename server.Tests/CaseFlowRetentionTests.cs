using Microsoft.Data.Sqlite;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class CaseFlowRetentionTests
{
    [Fact]
    public async Task CurrentWaitingFlowBlocksPurgeButSupersededRevisionDoesNot()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        _ = new CaseStore(db.Store);
        _ = new FlowLibrary(db.Store);
        var engine = new FlowEngine(db.Store);
        var policy = new FlowPolicy(new("wait", [new("output", FlowBlockKind.Output, [])]));
        await engine.ExecuteAsync(
            packet,
            policy,
            new Dictionary<FlowBlockKind, FlowBlockHandler>
            {
                [FlowBlockKind.Output] = (c, ct) => Task.FromResult(FlowBlockResult.Empty),
            },
            default
        );
        var retention = new CaseFlowRetention();
        using var connection = new SqliteConnection(db.Store.ConnectionString);
        connection.Open();
        using (var tx = connection.BeginTransaction())
            Assert.Contains(
                "Packet has active flow work.",
                retention.Blockers(connection, tx, packet)
            );
        packet.Revision = 2;
        packet.Status = "approved";
        db.Store.Save(packet);
        using (var tx = connection.BeginTransaction())
        {
            Assert.Empty(retention.Blockers(connection, tx, packet));
            retention.Purge(connection, tx, packet);
            tx.Commit();
        }
        packet.Revision = 1;
        Assert.Null(engine.Get(packet));
    }

    [Fact]
    public void RemovedCaseMembershipStillRetainsItsHistoricalSourceDependency()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var cases = new CaseStore(db.Store);
        _ = new FlowEngine(db.Store);
        _ = new FlowLibrary(db.Store);
        var record = cases.Create(new(packet.Id, 1, "S123", [packet.Documents[0].Id]), "alice", _ =>
            true
        );
        cases.RemoveMembers(
            record.Id,
            new(record.Revision, packet.Id, 1, [packet.Documents[0].Id]),
            "alice",
            _ => true
        );
        using var connection = new SqliteConnection(db.Store.ConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        var retention = new CaseFlowRetention();
        Assert.Contains(
            "Packet source provenance is retained in immutable case history.",
            retention.Blockers(connection, tx, packet)
        );
        Assert.Throws<WorkflowException>(() => retention.Purge(connection, tx, packet));
    }
}
