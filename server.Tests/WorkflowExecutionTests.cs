using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class WorkflowExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "workflow-execution-" + Guid.NewGuid().ToString("N")
    );
    private IConfiguration Config =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Paperwork:DataDirectory"] = root,
                    ["Paperwork:AzureEndpoint"] = "https://ocr.example.test",
                    ["Paperwork:AzureKey"] = "test",
                    ["Paperwork:JevApiKey"] = "test",
                    ["Paperwork:JevModel"] = "approved-model",
                }
            )
            .Build();
    private Store Store => new(Config);

    private static CatalogEntry Publish(Store store, CatalogEntry entry) =>
        store.Workflows.Publish(entry.Kind, entry.Id, entry.Version, entry.Revision);

    private CatalogEntry Workflow(
        Store store,
        string id,
        SplitPolicy policy,
        string typeId = "voucher",
        bool publish = true,
        bool draftDependencies = false
    )
    {
        var catalog = store.Workflows.Catalog();
        if (!catalog.Entries.Any(e => e.Kind == "type" && e.Id == typeId))
        {
            var type = store.Workflows.Create(
                new(
                    "type",
                    typeId,
                    "Voucher",
                    "Visible voucher",
                    new(["voucher"], "Use visible heading", new("inherit"))
                )
            );
            if (!draftDependencies)
                Publish(store, type);
        }
        var releaseId = id + "-release";
        var release = store.Workflows.Create(
            new(
                "release",
                releaseId,
                "Voucher release",
                "Test release",
                Release: new([new(typeId, 1), new("unknown", 1)])
            )
        );
        if (!draftDependencies)
            Publish(store, release);
        var workflow = store.Workflows.Create(
            new(
                "workflow",
                id,
                id,
                "Test workflow",
                Workflow: new(
                    new(releaseId, 1),
                    "simulation",
                    "default",
                    Simulation.Model,
                    "Recognize vouchers",
                    policy,
                    ["order"],
                    ["order"]
                )
            )
        );
        return publish ? Publish(store, workflow) : workflow;
    }

    private Packet Intake(Store store, string[] texts, string? key, WorkflowSelection selection)
    {
        var packet = new Packet { Name = "workflow-fixture.pdf" };
        var source = new Source
        {
            Name = "workflow-fixture.pdf",
            Extension = ".pdf",
            PageCount = texts.Length,
        };
        packet.Sources.Add(source);
        Directory.CreateDirectory(Path.Combine(root, packet.Id));
        Pdfs.Synthetic(store.SourcePath(packet, source), texts);
        source.Sha256 = Pdfs.SourceHash(store.SourcePath(packet, source));
        var result = store.Accept(packet, key, selection);
        Assert.True(result.Packet is not null, result.Error);
        return result.Packet!;
    }

    private async Task Process(Store store, string id)
    {
        using var processor = new PacketProcessor(
            store,
            new Providers(new Settings(Config), store, new NoNetwork()),
            new JobCoordinator()
        );
        await processor.Process(id, default);
    }

    [Fact]
    public async Task RetryAndReprocessPinSnapshotsWhileExplicitReprocessChangesWorkflow()
    {
        var store = Store;
        var entry = Workflow(store, "vouchers", new("consecutive"));
        var packet = Intake(
            store,
            ["voucher Order: A1", "voucher continuation"],
            "original",
            new(entry.Id, 1)
        );
        var hash = packet.Workflow!.Hash;
        packet.Status = "failed";
        packet.Error = "recoverable";
        store.Save(packet);
        var version = store.Workflows.NewVersion("workflow", entry.Id, 1, entry.Revision);
        version = store.Workflows.Edit(
            "workflow",
            version.Id,
            version.Version,
            version with
            {
                Workflow = version.Workflow! with { DefaultSplit = new("separate") },
            }
        );
        version = Publish(store, version);
        store.Workflows.SetDefault(new(version.Id, version.Version));
        store.Workflows.Archive("workflow", entry.Id, 1, entry.Revision);
        var retried = store.Retry(packet.Id).Packet!;
        Assert.Equal(hash, retried.Workflow!.Hash);
        await Process(Store, retried.Id);
        var ready = store.Get(packet.Id)!;
        Assert.Single(ready.Documents);
        Assert.Equal(hash, ready.Workflow!.Hash);
        Assert.All(ready.Pages, p => Assert.Equal("voucher", p.PredictedType));
        var retained = store.Reprocess(ready.Id, ready.Revision, new Settings(Config)).Packet!;
        Assert.Equal(hash, retained.Workflow!.Hash);
        var changed = store
            .Reprocess(
                ready.Id,
                ready.Revision,
                new Settings(Config),
                new(version.Id, version.Version)
            )
            .Packet!;
        Assert.NotEqual(hash, changed.Workflow!.Hash);
        Assert.Equal(ready.Id, changed.ParentPacketId);
        await Process(store, changed.Id);
        Assert.Equal(2, store.Get(changed.Id)!.Documents.Count);
        Assert.Equal(hash, store.Get(ready.Id)!.Workflow!.Hash);
    }

    [Fact]
    public void IdempotentReplayPrecedesChangedDefaultArchiveAndProviderReadiness()
    {
        var store = Store;
        var entry = Workflow(store, "idempotency", new("classifier"));
        store.Workflows.SetDefault(new(entry.Id, 1));
        var original = Intake(store, ["voucher"], "replay", new());
        var alternate = Workflow(store, "alternate", new("separate"));
        alternate = store.Workflows.NewVersion("workflow", alternate.Id, 1, alternate.Revision);
        alternate = store.Workflows.Edit(
            "workflow",
            alternate.Id,
            alternate.Version,
            alternate with
            {
                Workflow = alternate.Workflow! with { Mode = "live", Model = "approved-model" },
            }
        );
        alternate = Publish(store, alternate);
        store.Workflows.SetDefault(new(alternate.Id, alternate.Version));
        store.Workflows.Archive("workflow", entry.Id, 1, entry.Revision);
        var noCredentials = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Paperwork:DataDirectory"] = root }
            )
            .Build();
        var fresh = new Store(noCredentials);
        var replay = Intake(fresh, ["voucher"], "replay", new());
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(original.Workflow!.Hash, replay.Workflow!.Hash);
        var same = new Packet { Sources = original.Sources };
        Assert.Equal(409, fresh.Accept(same, "replay", new(alternate.Id, alternate.Version)).Code);
        Assert.Equal(200, fresh.Accept(same, "replay", new(entry.Id, 1)).Code);
        Assert.Equal(400, fresh.Accept(new Packet(), "new", new()).Code);
    }

    [Fact]
    public async Task PreviewFreezesDraftDependenciesRequiresExactRevisionAndCannotDeliver()
    {
        var store = Store;
        var draft = Workflow(
            store,
            "preview",
            new("consecutive"),
            publish: false,
            draftDependencies: true
        );
        Assert.Equal(
            409,
            store.Accept(new Packet(), null, new(draft.Id, 1, true, draft.Revision + 1)).Code
        );
        var p = Intake(store, ["voucher", "voucher"], null, new(draft.Id, 1, true, draft.Revision));
        var hash = p.Workflow!.Hash;
        var type = store
            .Workflows.Catalog()
            .Entries.Single(e => e.Kind == "type" && e.Id == "voucher");
        store.Workflows.Edit(
            type.Kind,
            type.Id,
            type.Version,
            type with
            {
                DocumentType = type.DocumentType! with { Keywords = ["different"] },
            }
        );
        await Process(Store, p.Id);
        p = Store.Get(p.Id)!;
        Assert.Equal(hash, p.Workflow!.Hash);
        Assert.All(p.Pages, page => Assert.Equal("voucher", page.PredictedType));
        Assert.True(p.TestRun);
        Assert.Single(p.Documents);
        Assert.Contains(p.Documents[0].ReviewReasons, reason => reason.Contains("Required order"));
        Assert.Equal(
            400,
            store
                .Review(
                    p.Id,
                    new(
                        1,
                        [new() { Type = "invoice", PageIds = p.Pages.Select(x => x.Id).ToList() }],
                        false
                    )
                )
                .Code
        );
        p = store.Review(p.Id, new(p.Revision, p.Documents, true)).Packet!;
        Assert.Equal(409, new DeliveryStore(store).Queue(p.Id, new(p.Revision)).Code);
        using var zip = new ZipArchive(new MemoryStream(Pdfs.Export(p, store)));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("testRun").GetBoolean());
        Assert.Equal(
            hash,
            manifest.RootElement.GetProperty("workflow").GetProperty("hash").GetString()
        );
        Assert.DoesNotContain(store.Workflows.Catalog().Stats, s =>
            s.Id == draft.Id && s.Packets != 0
        );
    }

    [Fact]
    public void LegacyProjectionDoesNotRewritePersistedPacketOrArtifact()
    {
        var store = Store;
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        const string original =
            "{\"id\":\"legacy\",\"status\":\"approved\",\"mode\":\"simulation\",\"revision\":7}";
        q.CommandText =
            "INSERT INTO packets VALUES('legacy',$json); INSERT INTO artifacts VALUES('legacy','original','immutable-artifact'); INSERT INTO revisions VALUES('legacy',2,$json);";
        q.Parameters.AddWithValue("$json", original);
        q.ExecuteNonQuery();
        var p = PacketResponse.From(store.Get("legacy")!);
        Assert.Equal(64, p.Workflow.Hash.Length);
        Assert.Equal(p.Workflow.Hash, PacketResponse.From(Store.Get("legacy")!).Workflow.Hash);
        Assert.All(store.History("legacy")!, h => Assert.Equal(p.Workflow.Hash, h.Workflow!.Hash));
        q.CommandText = "SELECT json FROM packets WHERE id='legacy'";
        Assert.Equal(original, q.ExecuteScalar());
        q.CommandText = "SELECT json FROM artifacts WHERE packet_id='legacy'";
        Assert.Equal("immutable-artifact", q.ExecuteScalar());
    }

    [Theory]
    [InlineData("classifier", 4)]
    [InlineData("separate", 4)]
    [InlineData("manual", 4)]
    [InlineData("consecutive", 2)]
    [InlineData("fixed", 2)]
    public void SplitPoliciesRespectSourceBoundariesAndPreserveEveryPage(string mode, int expected)
    {
        var p = PacketWithPolicy(new(mode, 2), "voucher", "voucher", "voucher", "voucher");
        p.Pages[2].SourceId = p.Pages[3].SourceId = "second";
        var docs = WorkflowAssembly.Assemble(p);
        Assert.Equal(expected, docs.Count);
        Assert.Equal(p.Pages.Select(x => x.Id), docs.SelectMany(x => x.PageIds));
        Assert.All(docs, d =>
            Assert.Single(
                d.PageIds.Select(id => p.Pages.Single(x => x.Id == id).SourceId).Distinct()
            )
        );
        Assert.All(docs, d => Assert.Contains("Manual approval required.", d.ReviewReasons));
    }

    [Fact]
    public void TypeOverridesAndMissingFixedOrMarkerEvidenceRequireReview()
    {
        var p = PacketWithPolicy(new("consecutive"), "voucher", "voucher", "voucher");
        p.Workflow = p.Workflow! with
        {
            Types = p
                .Workflow.Types.Select(t =>
                    t.Id == "voucher" ? t with { Split = new("fixed", 2) } : t
                )
                .ToArray(),
        };
        var docs = WorkflowAssembly.Assemble(p);
        Assert.Equal(new[] { 2, 1 }, docs.Select(x => x.PageIds.Count));
        Assert.Contains(docs[1].ReviewReasons, x => x.Contains("Incomplete fixed"));
        foreach (var mode in new[] { "first", "last" })
        {
            p = PacketWithPolicy(
                new(mode, Pattern: "MARK"),
                "voucher",
                "voucher MARK MARK",
                "voucher"
            );
            docs = WorkflowAssembly.Assemble(p);
            Assert.Contains(docs.SelectMany(d => d.ReviewReasons), x => x.Contains("Missing"));
            Assert.Contains(docs.SelectMany(d => d.ReviewReasons), x => x.Contains("Conflicting"));
            Assert.Equal(3, docs.Sum(d => d.PageIds.Count));
        }
    }

    [Fact]
    public void IdentifierUsesFirstCapturedGroupAndContinuationButFlagsMissingAndAmbiguousValues()
    {
        var p = PacketWithPolicy(
            new("identifier", Pattern: @"INV:([A-Z]\d+)\s+(copy\d+)"),
            "INV:A1 copy1",
            "INV:A1 copy2",
            "INV:B2 copy1",
            "continuation",
            "INV:C3 copy1 INV:D4 copy2"
        );
        p.Pages[3].StartProbability = 0;
        var docs = WorkflowAssembly.Assemble(p);
        Assert.Equal(new[] { 2, 2, 1 }, docs.Select(d => d.PageIds.Count));
        Assert.Contains(docs[1].ReviewReasons, x => x.Contains("Missing captured"));
        Assert.Contains(docs[2].ReviewReasons, x => x.Contains("Ambiguous captured"));
    }

    [Fact]
    public void RepeatedIdentifierCapturesCannotHideConflictingEvidence()
    {
        var p = PacketWithPolicy(
            new("identifier", Pattern: @"(?:Invoice:\s*([A-Z]+)\s*)+"),
            "Invoice: B",
            "Invoice: A Invoice: B"
        );
        var docs = WorkflowAssembly.Assemble(p);
        Assert.Equal(2, docs.Count);
        Assert.Contains(docs[1].ReviewReasons, x => x.Contains("Ambiguous captured"));
        Assert.Equal(p.Pages.Select(page => page.Id), docs.SelectMany(d => d.PageIds));
    }

    [Fact]
    public void CloneAndNewVersionPreserveClassificationUntilDefinitionsChange()
    {
        var store = Store;
        var clone = store.Workflows.Clone("workflow", "general-logistics", 1, 1, "Clone");
        clone = store.Workflows.Edit(
            "workflow",
            clone.Id,
            1,
            clone with
            {
                Workflow = clone.Workflow! with
                {
                    DefaultSplit = new("consecutive"),
                    IdentifierKinds = [],
                    RequiredIdentifierKinds = [],
                },
            }
        );
        clone = Publish(store, clone);
        var version = store.Workflows.NewVersion("workflow", "general-logistics", 1, 1);
        version = Publish(store, version);
        foreach (
            var reference in new[]
            {
                new VersionRef("general-logistics", 1),
                new VersionRef(clone.Id, 1),
                new VersionRef(version.Id, version.Version),
            }
        )
        {
            var packet = store
                .Accept(new Packet(), null, new(reference.Id, reference.Version))
                .Packet!;
            var page = new Page { Text = "symbol" };
            Simulation.Predict(page, packet.Workflow!);
            Assert.Equal("unknown", page.PredictedType);
            Assert.True(packet.Workflow!.LegacyRules);
        }
        var bol = store.Workflows.NewVersion("type", "bol", 1, 1);
        bol = store.Workflows.Edit(
            "type",
            "bol",
            bol.Version,
            bol with
            {
                DocumentType = bol.DocumentType! with { Keywords = ["symbol"] },
            }
        );
        bol = Publish(store, bol);
        var release = store.Workflows.NewVersion("release", "logistics", 1, 1);
        release = store.Workflows.Edit(
            "release",
            release.Id,
            release.Version,
            release with
            {
                Release = new(
                    release
                        .Release!.Types.Select(t =>
                            t.Id == "bol" ? new VersionRef("bol", bol.Version) : t
                        )
                        .ToArray()
                ),
            }
        );
        release = Publish(store, release);
        var customized = store.Workflows.NewVersion("workflow", clone.Id, 1, clone.Revision);
        customized = store.Workflows.Edit(
            "workflow",
            customized.Id,
            customized.Version,
            customized with
            {
                Workflow = customized.Workflow! with { Release = new(release.Id, release.Version) },
            }
        );
        customized = Publish(store, customized);
        var selected = store
            .Accept(new Packet(), null, new(customized.Id, customized.Version))
            .Packet!;
        var configuredPage = new Page { Text = "symbol" };
        Simulation.Predict(configuredPage, selected.Workflow!);
        Assert.Equal("bol", configuredPage.PredictedType);
        Assert.False(selected.Workflow!.LegacyRules);
    }

    [Fact]
    public void RegexTimeoutPreservesPagesAndAddsReviewReason()
    {
        var p = PacketWithPolicy(new("first", Pattern: "^(a+)+$"), new string('a', 30_000) + "!");
        var doc = Assert.Single(WorkflowAssembly.Assemble(p));
        Assert.Single(doc.PageIds);
        Assert.Contains(doc.ReviewReasons, x => x.Contains("timed out"));
    }

    [Fact]
    public void SimulationUsesConfiguredLiteralKeywordsAndRetainsExactLegacyMatching()
    {
        var page = new Page { Text = "voucher" };
        var workflow = PacketWithPolicy(new("classifier"), "").Workflow!;
        Simulation.Predict(page, workflow);
        Assert.Equal("voucher", page.PredictedType);
        Simulation.Predict(page, WorkflowRegistry.Legacy());
        Assert.Equal("unknown", page.PredictedType);
        page.Text = "bill   of lading";
        Simulation.Predict(page, WorkflowRegistry.Legacy());
        Assert.Equal("bol", page.PredictedType);
        page.Text = "invoiced";
        Simulation.Predict(page, WorkflowRegistry.Legacy());
        Assert.Equal("unknown", page.PredictedType);
    }

    [Fact]
    public async Task ProviderUsesPinnedCustomTaxonomyInstructionsAndModel()
    {
        var store = Store;
        var packet = PacketWithPolicy(new("classifier"), "voucher");
        packet.Workflow = packet.Workflow! with
        {
            Model = "pinned-approved-model",
            Instructions = "Inspect the voucher heading",
        };
        using var factory = new ProviderCapture();
        var providers = new Providers(new Settings(Config), store, factory);
        await providers.Classify(packet, packet.Pages[0], null, default);
        Assert.Equal("voucher", packet.Pages[0].PredictedType);
        using var request = JsonDocument.Parse(factory.Payload!);
        Assert.Equal("pinned-approved-model", request.RootElement.GetProperty("model").GetString());
        var type = request.RootElement.GetProperty("questions").GetProperty("documentType");
        Assert.Equal(
            new[] { "voucher", "unknown" },
            type.GetProperty("criteria").EnumerateObject().Select(x => x.Name)
        );
        Assert.Contains(
            "Inspect the voucher heading",
            type.GetProperty("instructions").GetString()
        );
        factory.Choice = "invoice";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Classify(packet, packet.Pages[0], null, default)
        );
    }

    private static Packet PacketWithPolicy(SplitPolicy policy, params string[] texts)
    {
        var workflow = WorkflowRegistry.Legacy() with
        {
            LegacyRules = false,
            DefaultSplit = policy,
            Types =
            [
                new(
                    "voucher",
                    1,
                    "Voucher",
                    "Voucher",
                    ["voucher"],
                    "Visible voucher",
                    new("inherit")
                ),
                new("unknown", 1, "Unknown", "Unknown", [], "", new("inherit")),
            ],
        };
        return new Packet
        {
            Workflow = workflow,
            Pages = texts
                .Select(
                    (text, index) =>
                        new Page
                        {
                            Text = text,
                            PredictedType = "voucher",
                            SourceId = "first",
                            PageNumber = index + 1,
                            StartProbability = 1,
                        }
                )
                .ToList(),
        };
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unexpected provider call.");
    }

    private sealed class ProviderCapture : HttpMessageHandler, IHttpClientFactory
    {
        public string? Payload;
        public string Choice = "voucher";

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Payload = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            answers = new
                            {
                                documentType = new { choice = Choice, confidence = .9 },
                                startsDocument = new { noul = 1 },
                            },
                        }
                    )
                ),
            };
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
