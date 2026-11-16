using System.Text.Json;
using Microsoft.Data.Sqlite;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class QualityPlatformTests
{
    [Fact]
    public void LegacyDefaultAndRawHeuristicScoresNeverAutoapprove()
    {
        using var db = new QualityDatabase();
        Assert.False(db.Quality.Route(db.Packet()).AutoApprove);
        var routed = db.Quality.Route(db.Packet(new(Mode: "selective")));
        Assert.False(routed.AutoApprove);
        Assert.Contains(routed.Items, i => i.Reason.Contains("Uncalibrated"));
        Assert.Throws<WorkflowException>(() =>
            QualityValidation.Validate(new(Mode: "selective", AutoApprove: true), null)
        );
    }

    [Fact]
    public void SamplingIsStableAndIndependentOfClassificationConfidence()
    {
        var policy = new QualityPolicy(SampleRate: .5);
        Assert.Equal(QualityStore.Sample("p", "h", policy), QualityStore.Sample("p", "h", policy));
        Assert.False(QualityStore.Sample("p", "h", policy with { SampleRate = 0 }));
        Assert.True(QualityStore.Sample("p", "h", policy with { SampleRate = 1 }));
        var sampled = Enumerable
            .Range(0, 1000)
            .Count(i => QualityStore.Sample(i.ToString(), "h", policy));
        Assert.InRange(sampled, 400, 600);
    }

    [Fact]
    public void EvaluatedRealProvenanceFixtureCanAutoapproveOfflineButSyntheticAndTestCannot()
    {
        using var db = new QualityDatabase();
        var model = db.Train();
        var policy = new QualityPolicy(Mode: "selective", AutoApprove: true, SampleRate: 1);
        var packet = db.Packet(policy, model);
        Assert.False(db.Quality.Route(packet).AutoApprove); // No actual model checkpoint yet.
        db.Training.Apply(packet);
        packet.Documents = WorkflowAssembly.Assemble(packet);
        var decision = db.Quality.Route(packet);
        Assert.True(decision.AutoApprove);
        Assert.Empty(db.Quality.Tasks(packet.Id));
        packet.Status = "approved";
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            db.Quality.OnProcessed(c, tx, packet, decision);
            tx.Commit();
        }
        var task = Assert.Single(db.Quality.Tasks(packet.Id));
        Assert.Equal(packet.Revision, task.PacketRevision);
        Assert.Equal("approved", task.Snapshot.Status);
        Assert.DoesNotContain(db.Training.Examples(), e => e.PacketId == packet.Id);
        packet.TestRun = true;
        Assert.False(db.Quality.Route(packet).AutoApprove);
        Assert.Throws<WorkflowException>(() =>
            QualityValidation.Validate(policy with { SyntheticEvaluation = true }, model)
        );
        var synthetic = db.Train(true);
        Assert.Throws<WorkflowException>(() => QualityValidation.Validate(policy, synthetic));
    }

    [Fact]
    public void BoundaryUncertaintyAndExternalValidationBlockAutomation()
    {
        using var db = new QualityDatabase();
        var model = db.Train();
        var policy = new QualityPolicy(Mode: "selective", AutoApprove: true);
        var packet = db.Packet(policy, model);
        db.Training.Apply(packet);
        Assert.False(db.Quality.Route(packet, ["Reference lookup is ambiguous."]).AutoApprove);
        packet.Workflow = packet.Workflow! with { DefaultSplit = new("classifier") };
        Assert.Contains(db.Quality.Route(packet).Items, i => i.Reason.Contains("Boundary policy"));
        Assert.Throws<WorkflowException>(() =>
            QualityValidation.Validate(policy with { Threshold = .99 }, model)
        );
    }

    [Fact]
    public void ReviewCaptureAndQaAreTransactionalAndSnapshotsAreIndependent()
    {
        using var db = new QualityDatabase();
        var before = db.Packet(new(SampleRate: 1));
        var after = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(before, Contract.Json),
            Contract.Json
        )!;
        after.Revision++;
        after.Status = "approved";
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            db.Quality.OnReview(c, tx, before, after, "primary");
            tx.Rollback();
        }
        Assert.Empty(db.Quality.Tasks());
        Assert.Empty(db.Training.Examples());
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            db.Quality.OnReview(c, tx, before, after, "primary");
            tx.Commit();
        }
        var task = Assert.Single(db.Quality.Tasks());
        after.Documents[0].Type = "pod";
        Assert.Equal("invoice", task.Snapshot.Documents[0].Type);
        Assert.False(Assert.Single(db.Training.Examples()).Synthetic); // Offline processing does not imply synthetic source.
        Assert.Throws<WorkflowException>(() =>
            db.Quality.Complete(task.Id, new(1, "pass"), "primary")
        );
        var completed = db.Quality.Complete(task.Id, new(1, "pass"), "independent");
        Assert.Equal("completed", completed.Status);
        Assert.Throws<WorkflowException>(() =>
            db.Quality.Complete(task.Id, new(1, "fail"), "another")
        );
    }

    [Fact]
    public void QualityFailureReopensOnlyMatchingApprovedRevision()
    {
        using var db = new QualityDatabase();
        var packet = db.Packet(new(SampleRate: 1));
        packet.Status = "approved";
        db.Store.Save(packet);
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            db.Quality.OnReview(c, tx, packet, packet, "primary");
            tx.Commit();
        }
        var task = Assert.Single(db.Quality.Tasks());
        db.Quality.Complete(task.Id, new(1, "fail", "Wrong type"), "independent");
        Assert.Equal("review", db.Store.Get(packet.Id)!.Status);
        Assert.Equal(2, db.Store.Get(packet.Id)!.Revision);
        Assert.Equal("invoice", task.Snapshot.Documents[0].Type);
    }

    [Fact]
    public void RetentionDeletesUntrainedExamplesButBlocksModelDependencies()
    {
        using var db = new QualityDatabase();
        var packet = db.Packet();
        db.Training.Import([
            new("packet-row", "invoice amount", "invoice", false, "test", packet.Id),
        ]);
        var retention = new QualityRetentionParticipant();
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            Assert.Empty(retention.Blockers(c, tx, packet));
            retention.Purge(c, tx, packet);
            tx.Commit();
        }
        Assert.Empty(db.Training.Examples());
        db.Train();
        db.Training.Import([
            new("packet-row", "invoice amount", "invoice", false, "test", packet.Id),
        ]);
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            Assert.NotEmpty(retention.Blockers(c, tx, packet));
            Assert.Throws<WorkflowException>(() => retention.Purge(c, tx, packet));
        }
    }
}
