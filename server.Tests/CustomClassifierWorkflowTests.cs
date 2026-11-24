using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class CustomClassifierWorkflowTests
{
    private const string Model = "custom-invoice-model";
    private const string Endpoint = "https://classifier-original.example.test/classify";
    private const string ReplacementEndpoint = "https://classifier-next.example.test/classify";
    private const string ApiKey = "custom-key-sentinel";
    private const string ModelSetting = "paprika:CustomClassifier:Model";
    private const string EndpointSetting = "paprika:CustomClassifier:Endpoint";
    private const string KeySetting = "paprika:CustomClassifier:ApiKey";

    [Fact]
    public void CatalogShowsCustomOnlyWithAModelAndReportsEndpointAndKeyReadiness()
    {
        using var db = new Database();
        ClassifierProviderInfo Provider() =>
            Assert.Single(db.Registry.Catalog().ClassifierProviders, p => p.Id == "custom");

        db.Config[ModelSetting] = " ";
        Assert.DoesNotContain(db.Registry.Catalog().ClassifierProviders, p => p.Id == "custom");
        db.Config[ModelSetting] = Model;
        db.Config[EndpointSetting] = null;
        Assert.Equal(new ClassifierProviderInfo("custom", Model, false), Provider());
        db.Config[EndpointSetting] = Endpoint;
        db.Config[KeySetting] = null;
        Assert.Equal(new ClassifierProviderInfo("custom", Model, false), Provider());

        // Draft configuration/publication does not require credentials; accepting a run does.
        var published = db.Publish(db.CreateDraft(db.CustomConfig));
        Assert.False(db.Entry(published).ClassifierBindingUnavailable);
        Assert.Equal(400, db.Store.Accept(new(), null, new(published.Id, published.Version)).Code);
        db.Config[KeySetting] = ApiKey;
        Assert.Equal(new ClassifierProviderInfo("custom", Model, true), Provider());
        Assert.Equal(200, db.Store.Accept(new(), null, new(published.Id, published.Version)).Code);
    }

    [Fact]
    public void DraftSavesValidateTheExplicitProviderAndPinOnlyServerDerivedHashes()
    {
        using var db = new Database();
        var config = db.CustomConfig with { ClassifierBindingHash = new string('0', 64) };
        Reject(() => db.CreateDraft(config with { ProviderProfile = "default" }));
        Reject(() => db.CreateDraft(config with { Model = "not-approved" }));
        db.Config[EndpointSetting] = null;
        Reject(() => db.CreateDraft(config));

        db.Config[EndpointSetting] = Endpoint;
        var saved = db.CreateDraft(config);
        Assert.Equal("custom", saved.Workflow!.ProviderProfile);
        Assert.Matches("^[a-f0-9]{64}$", saved.Workflow.ClassifierBindingHash!);
        Assert.Equal(db.Settings.CustomClassifierBindingHash, saved.Workflow.ClassifierBindingHash);
        Assert.NotEqual(config.ClassifierBindingHash, saved.Workflow.ClassifierBindingHash);

        var changed = db.Edit(
            saved with
            {
                ClassifierBindingUnavailable = true,
                Workflow = config with { ClassifierBindingHash = "client-supplied" },
            }
        );
        Assert.Equal(saved.Workflow.ClassifierBindingHash, changed.Workflow!.ClassifierBindingHash);
        Assert.False(db.Entry(changed).ClassifierBindingUnavailable);

        var simulation = db.Edit(
            changed with
            {
                Workflow = db.SimulationConfig with { ClassifierBindingHash = "client-supplied" },
            }
        );
        Assert.Null(simulation.Workflow!.ClassifierBindingHash);
        Assert.DoesNotContain(
            "classifierBindingHash",
            JsonSerializer.Serialize(simulation, Contract.Json)
        );
        Assert.Equal(
            "249a159757a77710ffa4243256e26f5f6c5072c5458a926c88543ffb6d02db46",
            WorkflowRegistry.Legacy().Hash
        );
    }

    [Theory]
    [InlineData(EndpointSetting, ReplacementEndpoint)]
    [InlineData(ModelSetting, "replacement-invoice-model")]
    public void StaleDraftAndCopiesCannotPublishUntilExplicitlyResaved(
        string setting,
        string replacement
    )
    {
        using var db = new Database();
        var published = db.Publish(db.CreateDraft(db.CustomConfig));
        var draft = db.Registry.NewVersion(published.Kind, published.Id, 1, published.Revision);
        Assert.Equal(
            published.Workflow!.ClassifierBindingHash,
            draft.Workflow!.ClassifierBindingHash
        );
        Assert.False(draft.ClassifierBindingNeedsRepair);

        db.Config[setting] = replacement;
        Assert.True(db.Entry(published).ClassifierBindingUnavailable);
        Assert.True(db.Entry(draft).ClassifierBindingUnavailable);
        Reject(() => db.Publish(draft));
        var next = db.Registry.NewVersion(published.Kind, published.Id, 1, published.Revision);
        var clone = db.Registry.Clone(published.Kind, published.Id, 1, published.Revision, "Clone");
        foreach (var copy in new[] { next, clone })
        {
            Assert.True(copy.ClassifierBindingNeedsRepair);
            Assert.Equal(
                published.Workflow.ClassifierBindingHash,
                copy.Workflow!.ClassifierBindingHash
            );
            Reject(() => db.Publish(copy));
        }

        foreach (var stale in new[] { draft, next, clone })
        {
            var updated = db.Publish(
                db.Edit(
                    stale with
                    {
                        Workflow = stale.Workflow! with
                        {
                            Model = db.Settings.CustomClassifierModel,
                        },
                    }
                )
            );
            Assert.False(updated.ClassifierBindingNeedsRepair);
            Assert.False(db.Entry(updated).ClassifierBindingUnavailable);
            Assert.Equal(
                db.Settings.CustomClassifierBindingHash,
                updated.Workflow!.ClassifierBindingHash
            );
            Assert.NotEqual(
                published.Workflow.ClassifierBindingHash,
                updated.Workflow.ClassifierBindingHash
            );
        }
        Assert.True(db.Entry(published).ClassifierBindingUnavailable);
        Assert.Equal(
            published.Workflow.ClassifierBindingHash,
            db.Entry(published).Workflow!.ClassifierBindingHash
        );
    }

    [Fact]
    public void IntakeAndReprocessKeepThePinnedSnapshotAcrossCredentialRotationOnly()
    {
        using var db = new Database();
        var entry = db.Publish(db.CreateDraft(db.CustomConfig));
        var selection = new WorkflowSelection(entry.Id, entry.Version);
        var accepted = db.Store.Accept(new(), "custom-intake", selection);
        Assert.Equal(200, accepted.Code);
        var packet = accepted.Packet!;
        var snapshot = packet.EffectiveWorkflow;
        Assert.Equal("custom", snapshot.ProviderProfile);
        Assert.Equal(Model, snapshot.Model);
        Assert.Equal(entry.Workflow!.ClassifierBindingHash, snapshot.ClassifierBindingHash);
        Assert.Equal(snapshot.Hash, db.Store.Get(packet.Id)!.EffectiveWorkflow.Hash);
        var publicJson = JsonSerializer.Serialize(
            new { Catalog = db.Registry.Catalog(), Packet = PacketResponse.From(packet) },
            Contract.Json
        );
        Assert.DoesNotContain("classifier-original.example.test", publicJson);
        Assert.DoesNotContain(ApiKey, publicJson);
        Assert.DoesNotContain("classifierBindingUnavailable", publicJson);

        packet = db.Store.Cancel(packet.Id, packet.Revision).Packet!;
        db.Config[KeySetting] = "rotated-test-key";
        Assert.False(db.Entry(entry).ClassifierBindingUnavailable);
        var retained = db.Store.Reprocess(packet.Id, packet.Revision, db.Settings);
        Assert.Equal(200, retained.Code);
        Assert.Equal(snapshot.Hash, retained.Packet!.EffectiveWorkflow.Hash);
        Assert.Equal(
            snapshot.ClassifierBindingHash,
            retained.Packet.EffectiveWorkflow.ClassifierBindingHash
        );

        db.Config[EndpointSetting] = ReplacementEndpoint;
        Assert.True(db.Entry(entry).ClassifierBindingUnavailable);
        var rejected = db.Store.Accept(new(), "new-upload", selection);
        Assert.Equal(400, rejected.Code);
        Assert.DoesNotContain(ReplacementEndpoint, rejected.Error!);
        Assert.Equal(400, db.Store.Accept(new() { Workflow = snapshot }, null).Code);
        Assert.Equal(400, db.Store.Reprocess(packet.Id, packet.Revision, db.Settings).Code);
        Assert.Equal(
            400,
            db.Store.Reprocess(packet.Id, packet.Revision, db.Settings, selection).Code
        );
        var replay = db.Store.Accept(new(), "custom-intake", selection);
        Assert.Equal(200, replay.Code);
        Assert.Equal(packet.Id, replay.Packet!.Id);
        Assert.Equal(snapshot.Hash, replay.Packet.EffectiveWorkflow.Hash);

        var draft = db.Registry.NewVersion(entry.Kind, entry.Id, entry.Version, entry.Revision);
        var replacement = db.Publish(db.Edit(draft));
        var changed = db.Store.Reprocess(
            packet.Id,
            packet.Revision,
            db.Settings,
            new(replacement.Id, replacement.Version)
        );
        Assert.Equal(200, changed.Code);
        Assert.Equal(packet.Id, changed.Packet!.ParentPacketId);
        Assert.Equal(
            db.Settings.CustomClassifierBindingHash,
            changed.Packet.EffectiveWorkflow.ClassifierBindingHash
        );
        Assert.NotEqual(snapshot.Hash, changed.Packet.EffectiveWorkflow.Hash);
        var original = db.Store.Get(packet.Id)!.EffectiveWorkflow;
        Assert.Equal(snapshot.Hash, original.Hash);
        Assert.Equal(snapshot.ClassifierBindingHash, original.ClassifierBindingHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mismatch")]
    public void UnboundOrMismatchedRetainedCustomSnapshotCannotStartANewRun(string? binding)
    {
        using var db = new Database();
        var published = db.Publish(db.CreateDraft(db.CustomConfig));
        var source = db.Store.Accept(new(), null, new(published.Id, published.Version)).Packet!;
        var unbound = new Packet
        {
            Status = "failed",
            Mode = "live",
            Workflow = source.EffectiveWorkflow with { ClassifierBindingHash = binding },
        };
        Assert.Equal(400, db.Store.Accept(unbound, null).Code);
        db.Store.Save(unbound);
        Assert.Equal(400, db.Store.Reprocess(unbound.Id, unbound.Revision, db.Settings).Code);
        Assert.Equal(binding, db.Store.Get(unbound.Id)!.EffectiveWorkflow.ClassifierBindingHash);
    }

    private static void Reject(Action action) =>
        Assert.Equal(400, Assert.Throws<WorkflowException>(action).Code);

    private sealed class Database : IDisposable
    {
        private readonly string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "paprika-custom-classifier-" + Guid.NewGuid().ToString("N")
        );
        public IConfiguration Config { get; }
        public Store Store { get; }
        public WorkflowRegistry Registry => Store.Workflows;
        public Settings Settings => new(Config);

        public Database()
        {
            Config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["paprika:DataDirectory"] = root,
                        ["paprika:AzureEndpoint"] = "https://ocr.example.test",
                        ["paprika:AzureKey"] = "ocr-test-key",
                        [EndpointSetting] = Endpoint,
                        [ModelSetting] = Model,
                        [KeySetting] = ApiKey,
                    }
                )
                .Build();
            Store = new(Config);
        }

        public WorkflowConfig SimulationConfig =>
            Registry.Catalog().Entries.Single(e => e.Id == "general-logistics").Workflow!;

        public WorkflowConfig CustomConfig =>
            SimulationConfig with
            {
                Mode = "live",
                ProviderProfile = "custom",
                Model = Model,
            };

        public CatalogEntry CreateDraft(WorkflowConfig config) =>
            Registry.Create(
                new("workflow", "custom-workflow", "Custom workflow", "", Workflow: config)
            );

        public CatalogEntry Entry(CatalogEntry entry) =>
            Registry
                .Catalog()
                .Entries.Single(e =>
                    e.Kind == entry.Kind && e.Id == entry.Id && e.Version == entry.Version
                );

        public CatalogEntry Edit(CatalogEntry entry) =>
            Registry.Edit(entry.Kind, entry.Id, entry.Version, entry);

        public CatalogEntry Publish(CatalogEntry entry) =>
            Registry.Publish(entry.Kind, entry.Id, entry.Version, entry.Revision);

        public void Dispose()
        {
            using var c = new SqliteConnection(Store.ConnectionString);
            SqliteConnection.ClearPool(c);
            Directory.Delete(root, true);
        }
    }
}
