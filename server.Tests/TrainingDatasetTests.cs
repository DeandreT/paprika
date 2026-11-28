using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static paprika.Tests.PlatformQualityIntegrationTests;

namespace paprika.Tests;

public sealed class TrainingDatasetTests
{
    private const string Training = "/api/training";
    private const string Datasets = Training + "/datasets";
    private static readonly TrainingDataset Legacy = new("default", "Legacy");

    private static TrainingExample[] Examples() =>
        [
            new("first", "amber ledger", "invoice", Provenance: "artificial ledger fixture"),
            new("second", "cedar signature", "pod", Provenance: "artificial delivery fixture"),
        ];

    private static Task<TrainingDataset> Create(HttpClient client, string name) =>
        Post<TrainingDataset>(client, Datasets, new TrainingDatasetRequest(name));

    private static string PathFor(TrainingDataset dataset) => $"{Datasets}/{dataset.Id}";

    private static Task<TrainingImportResponse> Import(
        HttpClient client,
        string path,
        params TrainingExample[] rows
    ) => Post<TrainingImportResponse>(client, path + "/examples", new TrainingImportRequest(rows));

    private static Task<TrainingModelResponse> Train(HttpClient client, string path) =>
        Post<TrainingModelResponse>(client, path + "/models", new TrainRequest("Fixture model"));

    [Fact]
    public async Task IdsTextConflictsAndIdempotentImportsAreScopedToTheSelectedDataset()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        Assert.Equal(Legacy, Assert.Single(await Get<TrainingDataset[]>(client, Datasets)));
        var first = await Create(client, "Finance");
        var creates = await Task.WhenAll(
            client.PostAsJsonAsync(Datasets, new TrainingDatasetRequest("Shipping")),
            client.PostAsJsonAsync(Datasets, new TrainingDatasetRequest("shipping"))
        );
        using var created = Assert.Single(creates, r => r.IsSuccessStatusCode);
        using var duplicate = Assert.Single(creates, r => r.StatusCode == HttpStatusCode.Conflict);
        var second = (await created.Content.ReadFromJsonAsync<TrainingDataset>())!;
        Assert.Equal(3, (await Get<TrainingDataset[]>(client, Datasets)).Length);
        Assert.NotEqual(first.Id, second.Id);
        var finance = PathFor(first);
        var shipping = PathFor(second);
        var original = Examples();
        TrainingExample[] financeRows =
        [
            .. original,
            original[0] with
            {
                Id = "third",
                Text = "AMBER, ledger!",
            },
        ];
        TrainingExample[] shippingRows =
        [
            original[0] with
            {
                Label = "pod",
            },
            original[1] with
            {
                Label = "receipt",
            },
        ];
        await Task.WhenAll(
            Import(client, finance, financeRows),
            Import(client, finance, financeRows),
            Import(client, shipping, shippingRows),
            Import(client, shipping, shippingRows)
        );
        Assert.Equal(financeRows, await Get<TrainingExample[]>(client, finance + "/examples"));
        Assert.Equal(shippingRows, await Get<TrainingExample[]>(client, shipping + "/examples"));
        Assert.Empty(await Get<TrainingExample[]>(client, Training + "/examples"));

        // A conflicting immutable ID rolls back the entire import, including a valid first row.
        using var conflict = await client.PostAsJsonAsync(
            finance + "/examples",
            new TrainingImportRequest([original[0] with { Id = "new" }, shippingRows[0]])
        );
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(financeRows, await Get<TrainingExample[]>(client, finance + "/examples"));
        var financeModel = await Train(client, finance);
        var shippingModel = await Train(client, shipping);
        Assert.Equal(first, financeModel.Artifact.Dataset);
        Assert.Equal(second, shippingModel.Artifact.Dataset);
        Assert.Equal(2, financeModel.Artifact.TrainingExamples);
        Assert.Equal(2, shippingModel.Artifact.TrainingExamples);
        Assert.Equal(financeRows.Length, financeModel.Artifact.DatasetExampleCount);
        Assert.Equal(
            TrainingClassifier.Hash(JsonSerializer.Serialize(financeRows, Contract.Json)),
            financeModel.Artifact.DatasetSnapshotHash
        );
        Assert.Equal(1, financeModel.Artifact.Counts["invoice"]["amber"]);
        Assert.Equal(1, shippingModel.Artifact.Counts["pod"]["amber"]);
        Assert.Equal(new[] { "pod", "receipt" }, shippingModel.Artifact.Labels);
        Assert.True(financeModel.Artifact.Synthetic);

        await Import(client, finance, original[0] with { Id = "conflict", Label = "pod" });
        using var badTraining = await client.PostAsJsonAsync(
            finance + "/models",
            new TrainRequest("Conflicting labels")
        );
        Assert.Equal(HttpStatusCode.BadRequest, badTraining.StatusCode);
        Assert.Equal(shippingModel.Hash, (await Train(client, shipping)).Hash);
    }

    [Fact]
    public async Task NamedSyntheticHeldoutModelCannotBeApprovedAndImportsRequireProvenance()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var dataset = await Create(client, "Artificial evaluation");
        var path = PathFor(dataset);
        using var invalid = await client.PostAsJsonAsync(
            path + "/examples",
            new TrainingImportRequest([new("invalid", "amber ledger", "invoice", Provenance: "")])
        );
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var rows = QualityDatabase.Examples(true);
        await Import(client, path, rows);
        var model = await Train(client, path);
        Assert.Equal(rows.Length, model.Artifact.DatasetExampleCount);
        Assert.Equal("held-out", model.Artifact.Evaluation.Status);
        Assert.True(model.Artifact.Synthetic);
        using var approval = await client.PostAsJsonAsync(
            $"{Training}/models/{model.Id}/approve",
            new TrainingApprovalRequest(model.Hash)
        );
        Assert.Equal(HttpStatusCode.BadRequest, approval.StatusCode);
        Assert.False(
            (
                await Post<TrainingModelRef>(
                    client,
                    Training + "/freeze",
                    new TrainingModelRef(model.Id)
                )
            ).Approved
        );
    }

    [Fact]
    public async Task NamedRoutesRequireAuthenticationAndAdministratorAccessInSharedMode()
    {
        await using var app = new QualityFactory();
        using var admin = app.Client();
        using var reviewer = app.Client("reviewer");
        using var anonymous = app.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-paprika", "1");
        var dataset = await Create(admin, "Administrator fixture");
        var path = PathFor(dataset);
        var changes = new TrainingDatasetRequest("Change");
        (HttpMethod Method, string Url, object? Body)[] routes =
        [
            (HttpMethod.Get, Datasets, null),
            (HttpMethod.Post, Datasets, changes),
            (HttpMethod.Put, path, new TrainingDatasetRenameRequest(changes.Name, dataset.Name)),
            (HttpMethod.Get, path + "/examples", null),
            (HttpMethod.Get, path + "/examples/summary", null),
            (HttpMethod.Post, path + "/examples", new TrainingImportRequest(Examples())),
            (HttpMethod.Post, path + "/models", new TrainRequest("Unauthorized model")),
        ];
        foreach (
            var (actor, status) in new[]
            {
                (reviewer, HttpStatusCode.Forbidden),
                (anonymous, HttpStatusCode.Unauthorized),
            }
        )
        foreach (var (method, url, body) in routes)
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = body is null ? null : JsonContent.Create(body),
            };
            using var result = await actor.SendAsync(request);
            Assert.Equal(status, result.StatusCode);
        }
        Assert.Contains(dataset, await Get<TrainingDataset[]>(admin, Datasets));
        Assert.Empty(await Get<TrainingExample[]>(admin, path + "/examples"));
    }

    [Fact]
    public async Task MissingAndUnknownNamedTargetsNeverReadWriteOrTrainAgainstLegacy()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var rows = Examples();
        await Import(client, Training, rows);
        foreach (string target in new[] { "", "no-such-dataset" })
        {
            string path = $"{Datasets}/{target}";
            using var list = await client.GetAsync(path + "/examples");
            using var import = await client.PostAsJsonAsync(
                path + "/examples",
                new TrainingImportRequest([rows[0] with { Id = "stray" }])
            );
            using var train = await client.PostAsJsonAsync(
                path + "/models",
                new TrainRequest("Wrong target")
            );
            AssertRejectedTarget(list);
            AssertRejectedTarget(import);
            AssertRejectedTarget(train);
        }
        using var query = await client.GetAsync(Training + "/examples?datasetId=no-such-dataset");
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        using var createWithId = await client.PostAsJsonAsync(
            Datasets,
            new { name = "Misplaced ID", datasetId = "no-such-dataset" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, createWithId.StatusCode);
        using var renameWithId = await client.PutAsJsonAsync(
            PathFor(Legacy),
            new
            {
                expectedName = Legacy.Name,
                name = "Wrong target",
                datasetId = "no-such-dataset",
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, renameWithId.StatusCode);
        foreach (var wrongId in new string?[] { null, "", "no-such-dataset" })
        {
            using var import = await client.PostAsJsonAsync(
                Training + "/examples",
                new { datasetId = wrongId, examples = new[] { rows[0] with { Id = "stray" } } }
            );
            using var train = await client.PostAsJsonAsync(
                PathFor(Legacy) + "/models",
                new { datasetId = wrongId, name = "Wrong target" }
            );
            Assert.Equal(HttpStatusCode.BadRequest, import.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, train.StatusCode);
        }
        Assert.Equal(rows, await Get<TrainingExample[]>(client, Training + "/examples"));
        Assert.Empty(await Get<TrainingModelResponse[]>(client, Training + "/models"));
        Assert.Equal(Legacy, Assert.Single(await Get<TrainingDataset[]>(client, Datasets)));
        var legacy = await Train(client, Training);
        Assert.Equal(Legacy, legacy.Artifact.Dataset);
        Assert.Equal(legacy.Hash, (await Train(client, PathFor(Legacy))).Hash);
        var release = await Publish(client, new(Training: new(legacy.Id)));
        var packet = await app.Upload(client, "amber ledger", new(release.Id, release.Version));
        await Review(client, packet, approve: true);
        Assert.Contains(await Get<TrainingExample[]>(client, Training + "/examples"), e =>
            e.PacketId == packet.Id
        );
    }

    private static void AssertRejectedTarget(HttpResponseMessage response) =>
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound }
        );

    [Fact]
    public async Task PublishedWorkflowRetainsTheModelDatasetNameAndContentsFromTraining()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var dataset = await Create(client, "Original corpus");
        var path = PathFor(dataset);
        await Import(client, path, Examples());
        var original = await Train(client, path);
        var release = await Publish(client, new(Training: new(original.Id)));
        var frozen = release.Workflow!.Capabilities!.Training!;
        Assert.Equal(original.Hash, frozen.Hash);
        var snapshot = TrainingStore.Resolve(frozen);
        Assert.Equal(dataset, snapshot.Dataset);
        Assert.Equal(original.Artifact.DatasetHash, snapshot.DatasetHash);
        Assert.Equal(2, snapshot.DatasetExampleCount);
        Assert.Equal(
            TrainingClassifier.Hash(JsonSerializer.Serialize(Examples(), Contract.Json)),
            snapshot.DatasetSnapshotHash
        );

        var rename = new TrainingDatasetRenameRequest("Revised corpus", dataset.Name);
        var renamed = await Put<TrainingDataset>(client, path, rename);
        Assert.Equal(new TrainingDataset(dataset.Id, "Revised corpus"), renamed);
        Assert.Equal(renamed, await Put<TrainingDataset>(client, path, rename));
        using var stale = await client.PutAsJsonAsync(
            path,
            new TrainingDatasetRenameRequest("Stale edit", dataset.Name)
        );
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(renamed, await Get<TrainingDataset[]>(client, Datasets));
        await Import(client, path, new TrainingExample("third", "violet voucher", "receipt"));
        var newer = await Train(client, path);
        Assert.Equal(renamed, newer.Artifact.Dataset);
        Assert.NotEqual(original.Artifact.DatasetHash, newer.Artifact.DatasetHash);
        Assert.NotEqual(snapshot.DatasetSnapshotHash, newer.Artifact.DatasetSnapshotHash);
        Assert.Equal(3, newer.Artifact.DatasetExampleCount);
        Assert.NotEqual(original.Hash, newer.Hash);
        var models = await Get<TrainingModelResponse[]>(client, Training + "/models");
        var retained = Assert.Single(models, m => m.Id == original.Id);
        Assert.Equal(original.Hash, retained.Hash);
        Assert.Equal(dataset, retained.Artifact.Dataset);
        Assert.Equal(snapshot.DatasetSnapshotHash, retained.Artifact.DatasetSnapshotHash);
        Assert.Equal(frozen, await Post<TrainingModelRef>(client, Training + "/freeze", frozen));

        var packet = await app.Upload(client, "amber ledger", new(release.Id, release.Version));
        Assert.Equal(frozen, packet.Workflow.Capabilities!.Training);
        Assert.Equal("invoice", Assert.Single(packet.Pages).PredictedType);
        await Review(client, packet, approve: true);
        // A model's training origin is metadata, never an implicit capture target.
        Assert.Equal(3, (await Get<TrainingExample[]>(client, path + "/examples")).Length);
        Assert.Empty(await Get<TrainingExample[]>(client, Training + "/examples"));
        Assert.Equal(2, (await Get<TrainingModelResponse[]>(client, Training + "/models")).Length);
    }

    [Fact]
    public async Task ReviewerCorrectionsOnlyEnterTheExplicitCollectionAndNeverTrainAModel()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var origin = await Create(client, "Model inputs");
        var target = await Create(client, "Reviewer corrections");
        var rows = Examples();
        await Import(client, PathFor(origin), rows);
        var seed = new TrainingExample("seed", "test invoice balance", "invoice");
        await Import(client, PathFor(target), seed);
        var model = await Train(client, PathFor(origin));
        var release = await Publish(
            client,
            new(Training: new(model.Id), ReviewCollection: new(target.Id))
        );
        var packet = await app.Upload(client, "amber ledger", new(release.Id, release.Version));
        Assert.Equal(target.Id, packet.Workflow.Capabilities!.ReviewCollection!.DatasetId);
        var corrected = Assert.Single(packet.Documents);
        Assert.Equal("invoice", corrected.Type);
        corrected.Type = "pod";
        var saved = await Review(client, packet, approve: false);
        var examples = PathFor(target) + "/examples";
        Assert.Equal(seed, Assert.Single(await Get<TrainingExample[]>(client, examples)));
        var approved = await Review(client, saved, approve: true);
        var collected = await Get<TrainingExample[]>(client, examples);
        // There are now two trainable labels; approval alone must still leave the model list alone.
        Assert.Equal(2, collected.Length);
        Assert.Contains(seed, collected);
        var row = Assert.Single(collected, e => e.PacketId == approved.Id);
        Assert.Equal(approved.Id, row.PacketId);
        Assert.Equal(approved.Revision, row.Revision);
        Assert.Equal("pod", row.Label);
        Assert.Equal(Assert.Single(approved.Pages).Text, row.Text);
        Assert.Equal("approved review by independent", row.Provenance);
        Assert.Equal(approved.Audit.Last(a => a.Action == "approved").At, row.ApprovedAt);
        Assert.False(string.IsNullOrWhiteSpace(row.SourcePage));
        Assert.False(row.Synthetic); // A non-preview upload in simulation, not a demo/test run.
        Assert.Equal(rows, await Get<TrainingExample[]>(client, PathFor(origin) + "/examples"));
        Assert.Empty(await Get<TrainingExample[]>(client, Training + "/examples"));
        Assert.Equal(
            model.Id,
            Assert.Single(await Get<TrainingModelResponse[]>(client, Training + "/models")).Id
        );
    }

    [Fact]
    public async Task InvalidExplicitCollectionCannotPublishOrSilentlyCollectDuringReview()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var store = app.Services.GetRequiredService<Store>();
        var invalid = new WorkflowCapabilities(ReviewCollection: new("missing-collection"));
        var draft = await WorkflowRequest(client, invalid);
        using var rejected = await client.PostAsJsonAsync("/api/workflows/catalog", draft);
        AssertRejectedTarget(rejected);
        foreach (object incomplete in new object[] { new { datasetId = (string?)null }, new { } })
        {
            var request = JsonSerializer.SerializeToNode(draft, Contract.Json)!;
            request["workflow"]!["capabilities"]!["reviewCollection"] =
                JsonSerializer.SerializeToNode(incomplete);
            using var response = await client.PostAsJsonAsync("/api/workflows/catalog", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Exercise the review transaction independently of the catalog's write-time validation.
        var legacy = WorkflowRegistry.Legacy("simulation");
        var packet = RetainedReviewPacket();
        packet.Workflow = legacy with { Capabilities = invalid };
        store.Save(packet);
        await Claim(client, PacketResponse.From(packet));
        using var review = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, true)
        );
        AssertRejectedTarget(review);
        var retained = await Get<PacketResponse>(client, $"/api/packets/{packet.Id}");
        Assert.Equal("review", retained.Status);
        Assert.Equal(packet.Revision, retained.Revision);
        Assert.Empty(await Get<TrainingExample[]>(client, Training + "/examples"));

        var historical = RetainedReviewPacket();
        store.Save(historical); // Older packets have no workflow binding or collection setting.
        var approved = await Review(client, PacketResponse.From(historical), approve: true);
        var row = Assert.Single(
            await Get<TrainingExample[]>(client, PathFor(Legacy) + "/examples")
        );
        Assert.Equal(approved.Id, row.PacketId);
        Assert.True(row.Synthetic); // Explicit preview provenance survives the legacy fallback.
        Assert.Empty(await Get<TrainingModelResponse[]>(client, Training + "/models"));
    }

    private static Packet RetainedReviewPacket()
    {
        var page = new Page { Text = "Artificial historical invoice" };
        return new Packet
        {
            Status = "review",
            TestRun = true,
            Pages = [page],
            Documents = [new Document { Type = "invoice", PageIds = [page.Id] }],
        };
    }

    [Theory]
    [InlineData("demo", true)]
    [InlineData("missing", true)]
    [InlineData("cycle", true)]
    [InlineData("live", false)]
    public async Task OlderReviewCapturesUseRetainedLineageDuringExportAndFit(
        string origin,
        bool expectedSynthetic
    )
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var store = app.Services.GetRequiredService<Store>();
        var parent = new Packet { Mode = origin == "demo" ? "demo" : "live" };
        var child = new Packet { Mode = "live", ParentPacketId = parent.Id };
        if (origin == "cycle")
            parent.ParentPacketId = child.Id;
        if (origin != "missing")
            store.Save(parent);
        store.Save(child);
        var dataset = await Create(client, "Retained approvals");
        var path = PathFor(dataset);
        var capture = new TrainingExample(
            $"review:{child.Id}:2:page",
            "invoice total due",
            "invoice",
            Synthetic: false,
            Provenance: "approved review by reviewer",
            PacketId: child.Id,
            Revision: 2
        );
        // An independently supplied label can also have a packet ID.
        var manual = new TrainingExample(
            "manual",
            "signed delivery",
            "pod",
            Synthetic: false,
            PacketId: child.Id
        );
        await Import(client, path, capture, manual);
        var expected = capture with { Synthetic = expectedSynthetic };
        Assert.Equal(
            new[] { manual, expected },
            await Get<TrainingExample[]>(client, path + "/examples")
        );
        var summaries = await Get<TrainingExampleSummary[]>(client, path + "/examples/summary");
        Assert.Equal(
            expectedSynthetic,
            Assert.Single(summaries, r => r.Id == capture.Id).Synthetic
        );
        Assert.False(Assert.Single(summaries, r => r.Id == manual.Id).Synthetic);
        var export = await Get<ReviewedDatasetExport>(client, path + "/export");
        Assert.Equal(
            expected,
            Assert.Single(export.Examples, r => r.Example.Id == capture.Id).Example
        );
        Assert.Equal(
            manual,
            Assert.Single(export.Examples, r => r.Example.Id == manual.Id).Example
        );
        Assert.Equal(expectedSynthetic, (await Train(client, path)).Artifact.Synthetic);
        await Import(client, path, capture, manual); // Original immutable bytes can still be replayed.
    }

    private static async Task<CreateCatalogRequest> WorkflowRequest(
        HttpClient client,
        WorkflowCapabilities capabilities
    )
    {
        var catalog = await Get<WorkflowCatalogResponse>(client, "/api/workflows");
        var original = catalog.Entries.Single(e =>
            e.Kind == "workflow" && e.Id == "general-logistics" && e.Version == 1
        );
        return new(
            "workflow",
            null,
            "Dataset test workflow",
            "Artificial fixture",
            Workflow: original.Workflow! with
            {
                Capabilities = capabilities,
                DefaultSplit = new("separate"),
            }
        );
    }

    private static async Task<CatalogEntry> Publish(
        HttpClient client,
        WorkflowCapabilities capabilities
    )
    {
        var draft = await Post<CatalogEntry>(
            client,
            "/api/workflows/catalog",
            await WorkflowRequest(client, capabilities)
        );
        return await Post<CatalogEntry>(
            client,
            $"/api/workflows/catalog/workflow/{draft.Id}/{draft.Version}/publish",
            new RevisionRequest(draft.Revision)
        );
    }

    private static async Task<PacketResponse> Review(
        HttpClient client,
        PacketResponse packet,
        bool approve
    )
    {
        await Claim(client, packet);
        var updated = await Put<PacketResponse>(
            client,
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, approve)
        );
        Assert.Equal(approve ? "approved" : "review", updated.Status);
        return updated;
    }

    // Pre-dataset v1 output for two synthetic single-token examples ("amber" and "cedar").
    // Keep the exact payload and its digest independent of today's serializer and model types.
    private const string OldArtifact =
        """{"algorithm":"multinomial-naive-bayes-text-v1","labels":["invoice","pod"],"counts":{"invoice":{"amber":1},"pod":{"cedar":1}},"documents":{"invoice":1,"pod":1},"totals":{"invoice":1,"pod":1},"vocabulary":2,"synthetic":true,"trainingExamples":2,"evaluation":{"status":"not evaluated","examples":0,"correct":0,"accuracy":null,"errorUpperBound":null,"threshold":0.95,"method":"deterministic held-out split; Wilson 95% error upper bound"},"datasetHash":"9267f705faadfbf1fff4faad4d3120456e407ec90c467deb87ab1332cd9d47ec"}""";
    private const string OldHash =
        "0f39dd8cd2591bd375a3d20f7a9e556423d8604d95ef088cae458837c1775154";
    private const string OldExample =
        """{"id":"a","text":"amber","label":"invoice","provenance":"old fixture","packetId":null,"revision":null}""";

    [Fact]
    public async Task ConcurrentAndRepeatedBootstrapPreservesLegacyRowsAndExactFrozenModelBytes()
    {
        await using var app = new QualityFactory();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["paprika:DataDirectory"] = app.Root }
            )
            .Build();
        var store = new Store(config);
        using (var db = new SqliteConnection(store.ConnectionString))
        {
            db.Open();
            using var seed = db.CreateCommand();
            seed.CommandText = """
                CREATE TABLE training_examples(id TEXT PRIMARY KEY,json TEXT NOT NULL);
                CREATE TABLE training_models(id TEXT PRIMARY KEY,json TEXT NOT NULL);
                CREATE TABLE training_predictions(packet_id TEXT NOT NULL,page_id TEXT NOT NULL,model_hash TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,page_id,model_hash));
                INSERT INTO training_examples VALUES
                  ('a',$example),
                  ('b','{"id":"b","text":"cedar","label":"pod","synthetic":true,"provenance":"old fixture","packetId":null,"revision":null,"sourcePage":null,"approvedAt":null}');
                INSERT INTO training_models VALUES ('old-model',$model);
                """;
            seed.Parameters.AddWithValue("$example", OldExample);
            seed.Parameters.AddWithValue(
                "$model",
                $$"""{"id":"old-model","name":"Old model","hash":"{{OldHash}}","approved":false,"approvedBy":null,"createdAt":"2025-01-01T00:00:00+00:00","artifact":{{OldArtifact}}}"""
            );
            seed.ExecuteNonQuery();
        }
        using var start = new ManualResetEventSlim();
        var bootstraps = Enumerable
            .Range(0, 4)
            .Select(_ =>
                Task.Run(() =>
                {
                    start.Wait();
                    return new TrainingStore(store).Datasets();
                })
            )
            .ToArray();
        start.Set();
        foreach (var catalog in await Task.WhenAll(bootstraps))
            Assert.Equal(Legacy, Assert.Single(catalog));

        using var client = app.Client(); // Full host startup opens the migrated database again.
        TrainingExample[] oldRows =
        [
            new("a", "amber", "invoice", Provenance: "old fixture"),
            new("b", "cedar", "pod", Provenance: "old fixture"),
        ];
        Assert.Equal(oldRows, await Get<TrainingExample[]>(client, Training + "/examples"));
        await Import(client, Training, oldRows);
        Assert.Equal(oldRows, await Get<TrainingExample[]>(client, PathFor(Legacy) + "/examples"));
        using (var db = new SqliteConnection(store.ConnectionString))
        {
            db.Open();
            using var raw = db.CreateCommand();
            raw.CommandText =
                "SELECT json FROM training_examples WHERE dataset_id='default' AND id='a'";
            Assert.Equal(OldExample, (string?)raw.ExecuteScalar());
        }
        var named = await Create(client, "After migration");
        await Import(client, PathFor(named), oldRows[0] with { Label = "receipt" });
        Assert.Equal(oldRows, await Get<TrainingExample[]>(client, Training + "/examples"));
        var summary = await Get<JsonElement[]>(client, PathFor(Legacy) + "/examples/summary");
        Assert.Equal(oldRows.Length, summary.Length);
        foreach (var row in oldRows)
        {
            var item = Assert.Single(summary, entry =>
                entry.GetProperty("id").GetString() == row.Id
            );
            Assert.Equal(
                new[] { "id", "label", "synthetic" },
                item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            );
            Assert.Equal(row.Label, item.GetProperty("label").GetString());
            Assert.True(item.GetProperty("synthetic").GetBoolean());
        }

        var oldModel = Assert.Single(
            await Get<TrainingModelResponse[]>(client, Training + "/models")
        );
        Assert.Null(oldModel.Artifact.Dataset);
        Assert.Null(oldModel.Artifact.DatasetSnapshotHash);
        Assert.Null(oldModel.Artifact.DatasetExampleCount);
        Assert.Equal(OldHash, oldModel.Hash);
        var existing = new TrainingModelRef("old-model", OldHash, OldArtifact);
        foreach (var input in new[] { new TrainingModelRef("old-model"), existing })
        {
            var frozen = await Post<TrainingModelRef>(client, Training + "/freeze", input);
            Assert.Equal(existing, frozen);
            Assert.Null(TrainingStore.Resolve(frozen).Dataset);
        }
        var release = await Publish(client, new(Training: existing));
        var packet = await app.Upload(client, "amber", new(release.Id, release.Version));
        Assert.Equal(existing, packet.Workflow.Capabilities!.Training);
        Assert.Equal("invoice", Assert.Single(packet.Pages).PredictedType);
        await Review(client, packet, approve: true);
        var migrated = await Get<TrainingExample[]>(client, Training + "/examples");
        Assert.Contains(migrated, e => e.PacketId == packet.Id);
        Assert.Equal(oldRows.Length + 1, migrated.Length);
        Assert.Equal(
            OldHash,
            Assert.Single(await Get<TrainingModelResponse[]>(client, Training + "/models")).Hash
        );
    }
}
