using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class TrainingPlatformTests
{
    [Fact]
    public void TinyDatasetTrainsButNeverClaimsEvaluation()
    {
        var model = TrainingClassifier.Fit(
            [
                new("a", "invoice payment due", "invoice"),
                new("b", "delivery signature received", "pod"),
            ],
            .95
        );
        Assert.Equal("not evaluated", model.Evaluation.Status);
        Assert.Null(model.Evaluation.Accuracy);
        Assert.Equal("invoice", TrainingClassifier.Predict(model, "invoice payment").Label);
        Assert.Equal("unknown", TrainingClassifier.Predict(model, "unseen vocabulary").Label);
    }

    [Fact]
    public void IdenticalTextCannotLeakOrCarryConflictingLabels()
    {
        Assert.Throws<WorkflowException>(() =>
            TrainingClassifier.Fit(
                [new("a", "Same text", "invoice"), new("b", "same TEXT", "pod")],
                .95
            )
        );
        var examples = Enumerable
            .Range(0, 50)
            .Select(i => new TrainingExample("a" + i, "invoice payment", "invoice"))
            .Concat(
                Enumerable
                    .Range(0, 50)
                    .Select(i => new TrainingExample("b" + i, "delivery signature", "pod"))
            )
            .ToArray();
        var model = TrainingClassifier.Fit(examples, .95);
        Assert.Equal(2, model.TrainingExamples);
        Assert.Equal("not evaluated", model.Evaluation.Status);
    }

    [Fact]
    public void SyntheticHeldoutCannotBeApprovedAndFrozenPayloadIsHashVerified()
    {
        using var db = new QualityDatabase();
        db.Training.Import(QualityDatabase.Examples(true));
        var model = db.Training.Train(new("Synthetic example"));
        Assert.Equal("held-out", model.Artifact.Evaluation.Status);
        Assert.Throws<WorkflowException>(() =>
            db.Training.Approve(model.Id, model.Hash, "reviewer")
        );
        var frozen = db.Training.Freeze(new(model.Id));
        Assert.Equal(model.Hash, frozen.Hash);
        Assert.Throws<WorkflowException>(() =>
            TrainingStore.Resolve(frozen with { Payload = frozen.Payload + " " })
        );
    }

    [Fact]
    public void ExamplesAreImmutableAndFreezeUsesExistingTransaction()
    {
        using var db = new QualityDatabase();
        db.Training.Import(QualityDatabase.Examples(false));
        var model = db.Training.Train(new("Real provenance fixture"));
        db.Training.Approve(model.Id, model.Hash, "reviewer");
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var frozen = TrainingPlatform.Freeze(c, tx, new(model.Id));
        Assert.True(frozen.Approved);
        Assert.Equal(model.Hash, frozen.Hash);
        tx.Commit();
        var row = db.Training.Examples()[0];
        Assert.Throws<WorkflowException>(() => db.Training.Import([row with { Text = "changed" }]));
    }

    [Fact]
    public void HeldoutIsRepeatableAndReportsConservativeErrorBound()
    {
        var examples = QualityDatabase.Examples(false);
        var first = TrainingClassifier.Fit(examples, .95);
        var second = TrainingClassifier.Fit(examples.Reverse().ToArray(), .95);
        Assert.Equal(first.DatasetHash, second.DatasetHash);
        Assert.Equal(first.Evaluation, second.Evaluation);
        Assert.Equal("held-out", first.Evaluation.Status);
        Assert.True(first.Evaluation.ErrorUpperBound > 0);
        Assert.True(first.Evaluation.ErrorUpperBound < .1);
    }

    [Fact]
    public void LaterApprovalCannotChangePreviouslyFrozenReference()
    {
        using var db = new QualityDatabase();
        db.Training.Import(QualityDatabase.Examples(false));
        var model = db.Training.Train(new("Approval isolation fixture"));
        var frozen = db.Training.Freeze(new(model.Id));
        Assert.False(frozen.Approved);
        Assert.Throws<WorkflowException>(() => db.Training.Freeze(frozen with { Approved = true }));
        db.Training.Approve(model.Id, model.Hash, "admin");
        Assert.Equal(frozen, db.Training.Freeze(frozen));
        Assert.True(db.Training.Freeze(new(model.Id)).Approved);
    }
}

internal sealed class QualityDatabase : IDisposable
{
    public string Root { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "quality-test-" + Guid.NewGuid().ToString("N")
        );
    public Store Store { get; }
    public TrainingStore Training { get; }
    public QualityStore Quality { get; }

    public QualityDatabase()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["paprika:DataDirectory"] = Root }
            )
            .Build();
        Store = new(config);
        Training = new(Store);
        Quality = new(Store, Training);
    }

    // Entirely artificial test fixture. false is used only to exercise real-provenance validation branches;
    // these rows never leave this isolated disposable test database and establish no operational accuracy.
    public static TrainingExample[] Examples(bool synthetic) =>
        Enumerable
            .Range(0, 200)
            .SelectMany(i =>
                new[]
                {
                    new TrainingExample(
                        "invoice-" + i,
                        $"invoice payment amount account due invoice payment uniqueinv{i}",
                        "invoice",
                        synthetic,
                        "isolated test fixture"
                    ),
                    new TrainingExample(
                        "pod-" + i,
                        $"delivery signed received driver proof delivery signed uniquepod{i}",
                        "pod",
                        synthetic,
                        "isolated test fixture"
                    ),
                }
            )
            .ToArray();

    public TrainingModelRef Train(bool synthetic = false)
    {
        Training.Import(
            Examples(synthetic)
                .Select(e => synthetic ? e with { Id = "synthetic-" + e.Id } : e)
                .ToArray()
        );
        var model = Training.Train(new("Isolated fixture"));
        if (!synthetic)
            Training.Approve(model.Id, model.Hash, "admin");
        return Training.Freeze(new(model.Id));
    }

    public Packet Packet(QualityPolicy? policy = null, TrainingModelRef? model = null)
    {
        var baseline = WorkflowRegistry.Legacy("simulation");
        var packet = new Packet
        {
            Name = "Quality fixture",
            Mode = "simulation",
            Workflow = baseline with
            {
                DefaultSplit = new("separate"),
                Capabilities =
                    policy is null && model is null ? null : new(Quality: policy, Training: model),
            },
            Pages =
            [
                new()
                {
                    Text = "invoice payment account due invoice",
                    PredictedType = "invoice",
                    Confidence = .99,
                    StartProbability = 1,
                },
            ],
        };
        packet.Documents = WorkflowAssembly.Assemble(packet);
        return packet;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}
