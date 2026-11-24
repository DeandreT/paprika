using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class WorkflowRegistryTests
{
    [Fact]
    public void InitializationPreservesEditsAndDefaultAcrossRestart()
    {
        using var db = new Database();
        var original = db.Registry.Catalog();
        Assert.Equal(new VersionRef("general-logistics", 1), original.DefaultWorkflow);
        Assert.Equal(
            Contract.Types,
            original
                .Entries.Single(e => e.Kind == "release" && e.Id == "logistics")
                .Release!.Types.Select(t => t.Id)
        );
        Assert.Equal(6, original.Entries.Count(e => e.Kind == "type" && e.Status == "published"));
        var draft = db.Entry("workflow", "invoice-processing");
        var changed = db.Registry.Edit(
            draft.Kind,
            draft.Id,
            draft.Version,
            draft with
            {
                Name = "Saved invoice draft",
            }
        );
        var clone = db.Registry.Clone("workflow", "general-logistics", 1, 1, "New default");
        clone = db.Registry.Publish(clone.Kind, clone.Id, clone.Version, clone.Revision);
        db.Registry.SetDefault(new(clone.Id, clone.Version));
        var reopened = new Store(db.Configuration).Workflows;
        reopened.Initialize();
        var catalog = reopened.Catalog();
        Assert.Equal(original.Entries.Length + 1, catalog.Entries.Length);
        Assert.Equal(
            JsonSerializer.Serialize(changed, Contract.Json),
            JsonSerializer.Serialize(
                catalog.Entries.Single(e => e.Kind == changed.Kind && e.Id == changed.Id),
                Contract.Json
            )
        );
        Assert.Equal(new VersionRef(clone.Id, 1), catalog.DefaultWorkflow);
    }

    [Theory]
    [InlineData("simulation", "simulation", Simulation.Model)]
    [InlineData("demo", "simulation", Simulation.Model)]
    [InlineData("live", "live", "approved-model")]
    public void SeedsUseServerModeAndApprovedModel(
        string mode,
        string expectedMode,
        string expectedModel
    )
    {
        using var db = new Database(mode);
        var config = db.Entry("workflow", "general-logistics").Workflow!;
        Assert.Equal(expectedMode, config.Mode);
        Assert.Equal(expectedModel, config.Model);
        Assert.Equal(expectedMode == "live" ? "jev" : "default", config.ProviderProfile);
        Assert.All(
            db.Registry.Catalog().Entries.Where(e => e.Kind == "workflow" && e.Status == "draft"),
            e =>
            {
                Assert.Equal("simulation", e.Workflow!.Mode);
                Assert.Equal("default", e.Workflow.ProviderProfile);
            }
        );
    }

    [Fact]
    public void PublishedVersionsAreImmutableAndNewVersionsDoNotChangeOldSnapshots()
    {
        using var db = new Database();
        var before = db.Resolve();
        var published = db.Entry("workflow", "general-logistics");
        Error(409, () =>
            db.Registry.Edit(
                published.Kind,
                published.Id,
                published.Version,
                published with
                {
                    Name = "Changed",
                }
            )
        );
        var version = db.Registry.NewVersion(published.Kind, published.Id, 1, published.Revision);
        Assert.Equal(2, version.Version);
        version = db.Registry.Edit(
            version.Kind,
            version.Id,
            2,
            version with
            {
                Workflow = version.Workflow! with { DefaultSplit = new("fixed", 2) },
            }
        );
        var preview = db.Resolve(new(version.Id, 2, true, version.Revision));
        Assert.True(preview.LegacyRules);
        version = db.Registry.Publish(version.Kind, version.Id, 2, version.Revision);
        db.Registry.SetDefault(new(version.Id, 2));
        Assert.Equal(2, db.Resolve().Version);
        Assert.Equal(before.Hash, db.Resolve(new(published.Id, 1)).Hash);
        Assert.NotEqual(before.Hash, preview.Hash);
        Error(409, () => db.Registry.Edit(version.Kind, version.Id, version.Version, version));
    }

    [Fact]
    public void DraftPreviewFreezesSavedDraftDependenciesAndPublicationRequiresPublishedGraph()
    {
        using var db = new Database();
        var type = db.Registry.Create(
            new(
                "type",
                "custom",
                "Custom",
                "Custom type",
                DocumentType: new(["dispatch"], "Identify dispatch forms.", new())
            )
        );
        var release = db.Registry.Create(
            new(
                "release",
                "custom-release",
                "Custom release",
                "",
                Release: new([new(type.Id, 1), new("unknown", 1)])
            )
        );
        var workflow = db.Registry.Create(
            new(
                "workflow",
                "custom-workflow",
                "Custom workflow",
                "",
                Workflow: db.Entry("workflow", "general-logistics").Workflow! with
                {
                    Release = new(release.Id, 1),
                }
            )
        );
        Error(409, () => db.Registry.Publish("workflow", workflow.Id, 1, 1));
        Error(409, () => db.Registry.Publish("release", release.Id, 1, 1));
        var frozen = db.Resolve(new(workflow.Id, 1, true, 1));
        Assert.Equal("dispatch", frozen.Types[0].Keywords.Single());
        type = db.Registry.Edit(
            "type",
            type.Id,
            1,
            type with
            {
                DocumentType = type.DocumentType! with { Keywords = ["warehouse"] },
            }
        );
        var later = db.Resolve(new(workflow.Id, 1, true, 1));
        Assert.Equal("dispatch", frozen.Types[0].Keywords.Single());
        Assert.NotEqual(frozen.Hash, later.Hash);
        type = db.Registry.Publish("type", type.Id, 1, type.Revision);
        release = db.Registry.Publish("release", release.Id, 1, release.Revision);
        workflow = db.Registry.Publish("workflow", workflow.Id, 1, workflow.Revision);
        Assert.Equal(later.Hash, db.Resolve(new(workflow.Id, 1)).Hash);
        Error(409, () => db.Resolve(new(workflow.Id, 1, true, workflow.Revision)));
    }

    [Fact]
    public void ArchiveProtectsDefaultAndRetainsPublishedDependencyResolution()
    {
        using var db = new Database();
        var original = db.Resolve();
        Error(409, () => db.Registry.Archive("workflow", original.Id, 1, 1));
        db.Registry.Archive("type", "bol", 1, 1);
        Assert.Equal(original.Hash, db.Resolve().Hash);
        var copy = db.Registry.Clone("workflow", original.Id, 1, 1, "Copied workflow");
        Error(409, () => db.Registry.Publish(copy.Kind, copy.Id, 1, copy.Revision));
        var other = db.Registry.Clone("workflow", "invoice-processing", 1, 1, "Invoices");
        db.Registry.Publish("release", "invoice-documents", 1, 1);
        other = db.Registry.Publish(other.Kind, other.Id, 1, 1);
        db.Registry.SetDefault(new(other.Id, 1));
        var archived = db.Registry.Archive("workflow", original.Id, 1, 1);
        Assert.Equal("archived", archived.Status);
        Error(409, () => db.Resolve(new(original.Id, 1)));
        Error(409, () => db.Registry.SetDefault(new(original.Id, 1)));
        Assert.Equal("bill of lading", original.Types.Single(t => t.Id == "bol").Keywords[0]);
    }

    [Fact]
    public async Task ConcurrentEditsUseDatabaseRevisionAcrossRegistryInstances()
    {
        using var db = new Database();
        var draft = db.Entry("workflow", "receiving");
        var second = new Store(db.Configuration).Workflows;
        using var start = new ManualResetEventSlim(false);
        Task<int> Edit(WorkflowRegistry registry, string name) =>
            Task.Run(() =>
            {
                start.Wait();
                try
                {
                    registry.Edit(draft.Kind, draft.Id, 1, draft with { Name = name });
                    return 200;
                }
                catch (WorkflowException ex)
                {
                    return ex.Code;
                }
            });
        var first = Edit(db.Registry, "First editor");
        var other = Edit(second, "Second editor");
        start.Set();
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(first, other)).Order().ToArray());
        Assert.Equal(2, db.Entry("workflow", "receiving").Revision);
        Error(409, () => db.Registry.Publish(draft.Kind, draft.Id, 1, 1));
        Error(409, () => db.Registry.Archive(draft.Kind, draft.Id, 1, 1));
        Error(409, () => db.Registry.Clone(draft.Kind, draft.Id, 1, 1, "Stale clone"));
        Error(409, () => db.Registry.NewVersion(draft.Kind, draft.Id, 1, 1));
    }

    [Fact]
    public void NullAndMalformedDefinitionsReturnValidationErrorsWithoutSaving()
    {
        using var db = new Database();
        var good = new CreateCatalogRequest(
            "type",
            "valid",
            "Valid",
            "",
            DocumentType: new([], "", new())
        );
        CreateCatalogRequest[] invalid =
        [
            null!,
            good with
            {
                Kind = null!,
            },
            good with
            {
                Kind = "model",
            },
            good with
            {
                Id = "Uppercase",
            },
            good with
            {
                Id = "",
            },
            good with
            {
                Id = "contains space",
            },
            good with
            {
                Id = new string('a', 81),
            },
            good with
            {
                Id = "new\n",
            },
            good with
            {
                Name = null!,
            },
            good with
            {
                Name = " ",
            },
            good with
            {
                Name = new string('a', 121),
            },
            good with
            {
                Description = null!,
            },
            good with
            {
                Description = new string('a', 2001),
            },
            good with
            {
                DocumentType = null,
            },
            good with
            {
                Release = new([new("unknown", 1)]),
            },
            good with
            {
                DocumentType = new(null!, "", new()),
            },
            good with
            {
                DocumentType = new([null!], "", new()),
            },
            good with
            {
                DocumentType = new([""], "", new()),
            },
            good with
            {
                DocumentType = new(["invoice", "INVOICE"], "", new()),
            },
            good with
            {
                DocumentType = new([new string('a', 201)], "", new()),
            },
            good with
            {
                DocumentType = new([], null!, new()),
            },
            good with
            {
                DocumentType = new([], new string('a', 8001), new()),
            },
            good with
            {
                DocumentType = new([], "", null!),
            },
            good with
            {
                Id = "unknown",
                DocumentType = new(["match"], "", new()),
            },
        ];
        var count = db.Registry.Catalog().Entries.Length;
        foreach (var request in invalid)
            Error(400, () => db.Registry.Create(request));
        Assert.Equal(count, db.Registry.Catalog().Entries.Length);
        Error(400, () => db.Registry.Edit("workflow", "receiving", 1, null!));
        Error(400, () => db.Registry.SetDefault(null!));
        Error(404, () => db.Registry.Publish("type", "missing", 1, 1));
    }

    [Fact]
    public void ReferencesRequireExistingExactUniqueTypesIncludingUnknown()
    {
        using var db = new Database();
        VersionRef[][] invalid =
        [
            null!,
            [],
            [null!],
            [new("invoice", 1)],
            [new("unknown", 0)],
            [new("unknown", 1), new("missing", 1)],
            [new("unknown", 1), new("invoice", 2)],
            [new("unknown", 1), new("unknown", 1)],
            [new("unknown", 1), new(null!, 1)],
        ];
        foreach (var types in invalid)
            Error(400, () =>
                db.Registry.Create(
                    new("release", "invalid-release", "Invalid", "", Release: new(types))
                )
            );
        var config = db.Entry("workflow", "general-logistics").Workflow!;
        Error(400, () =>
            db.Registry.Create(
                new(
                    "workflow",
                    "bad",
                    "Invalid",
                    "",
                    Workflow: config with
                    {
                        Release = new("missing", 1),
                    }
                )
            )
        );
        Error(400, () =>
            db.Registry.Create(
                new("workflow", "bad", "Invalid", "", Workflow: config with { Release = null! })
            )
        );
        var type = db.Registry.NewVersion("type", "unknown", 1, 1);
        Error(400, () =>
            db.Registry.Create(
                new(
                    "release",
                    "bad",
                    "Invalid",
                    "",
                    Release: new([new("unknown", 1), new("unknown", type.Version)])
                )
            )
        );
    }

    [Fact]
    public void WorkflowConfigRejectsUnapprovedProvidersModelsAndIdentifierKinds()
    {
        using var db = new Database();
        var good = db.Entry("workflow", "general-logistics").Workflow!;
        WorkflowConfig[] invalid =
        [
            good with
            {
                Mode = "demo",
            },
            good with
            {
                Mode = null!,
            },
            good with
            {
                ProviderProfile = "external",
            },
            good with
            {
                ProviderProfile = null!,
            },
            good with
            {
                Model = null!,
            },
            good with
            {
                Model = "unapproved",
            },
            good with
            {
                Mode = "live",
                Model = "unapproved",
            },
            good with
            {
                Instructions = null!,
            },
            good with
            {
                DefaultSplit = new(),
            },
            good with
            {
                IdentifierKinds = null!,
            },
            good with
            {
                RequiredIdentifierKinds = null!,
            },
            good with
            {
                IdentifierKinds = ["load", "load"],
            },
            good with
            {
                IdentifierKinds = ["customer"],
            },
            good with
            {
                IdentifierKinds = ["Load"],
            },
            good with
            {
                RequiredIdentifierKinds = ["customer"],
            },
            good with
            {
                IdentifierKinds = ["load"],
                RequiredIdentifierKinds = ["receipt"],
            },
        ];
        foreach (var config in invalid)
            Error(400, () =>
                db.Registry.Create(new("workflow", "bad", "Invalid", "", Workflow: config))
            );
        var created = db.Registry.Create(
            new(
                "workflow",
                null,
                "Empty identifiers",
                "",
                Workflow: good with
                {
                    IdentifierKinds = [],
                    RequiredIdentifierKinds = [],
                }
            )
        );
        Assert.NotEmpty(created.Id);
    }

    [Theory]
    [InlineData("inherit", 1, "", false)]
    [InlineData("fixed", 0, "", true)]
    [InlineData("fixed", 10001, "", true)]
    [InlineData("separate", 2, "", true)]
    [InlineData("classifier", 1, "marker", true)]
    [InlineData("first", 1, "", true)]
    [InlineData("last", 1, "(", true)]
    [InlineData("identifier", 1, "(?:value)", true)]
    [InlineData("identifier", 1, "(?<2>value)", true)]
    [InlineData("unsupported", 1, "", true)]
    [InlineData(null, 1, "", true)]
    [InlineData("classifier", 1, null, true)]
    public void InvalidSplitPoliciesAreRejected(
        string? mode,
        int count,
        string? pattern,
        bool inherit
    ) => Error(400, () => WorkflowRegistry.ValidatePolicy(new(mode!, count, pattern!), inherit));

    [Fact]
    public void RegexCaptureValidationAndDangerousRulesHaveFiniteExecutionBudget()
    {
        WorkflowRegistry.ValidatePolicy(new("identifier", Pattern: @"Order\s+(\w+)"), false);
        WorkflowRegistry.ValidatePolicy(new("identifier", Pattern: @"(?<reference>\w+)"), false);
        WorkflowRegistry.ValidatePolicy(new("fixed", 3), false);
        Error(400, () => WorkflowRegistry.ValidatePolicy(null!, true));
        Error(400, () => WorkflowRegistry.CompileRule(null!));
        Error(400, () => WorkflowRegistry.CompileRule(new string('a', 2001)));
        var rule = WorkflowRegistry.CompileRule("^(a+)+$");
        Assert.Equal(TimeSpan.FromMilliseconds(100), rule.MatchTimeout);
        Assert.Throws<RegexMatchTimeoutException>(() =>
            rule.IsMatch(new string('a', 100000) + "!")
        );
    }

    [Fact]
    public void PreviewRequiresSavedExactDraftRevisionAndLiveReadinessIsCheckedAtResolution()
    {
        using var db = new Database();
        var draft = db.Entry("workflow", "receiving");
        Error(400, () => db.Resolve(new(draft.Id, 1, true)));
        Error(400, () => db.Resolve(new(null, null, true, 1)));
        Error(400, () => db.Resolve(new(draft.Id)));
        Error(400, () => db.Resolve(new(null, 1)));
        Error(400, () => db.Resolve(new(draft.Id, 1, false, 1)));
        Error(409, () => db.Resolve(new(draft.Id, 1)));
        Error(409, () => db.Resolve(new(draft.Id, 1, true, 2)));
        draft = db.Registry.Edit(draft.Kind, draft.Id, 1, draft with { Name = "Revision two" });
        Error(409, () => db.Resolve(new(draft.Id, 1, true, 1)));
        Assert.Equal(draft.Name, db.Resolve(new(draft.Id, 1, true, 2)).Name);
        var live = db.Registry.Clone("workflow", "general-logistics", 1, 1, "Live");
        live = db.Registry.Edit(
            live.Kind,
            live.Id,
            1,
            live with
            {
                Workflow = live.Workflow! with { Mode = "live", Model = "approved-model" },
            }
        );
        Error(400, () => db.Resolve(new(live.Id, 1, true, live.Revision)));
        live = db.Registry.Publish(live.Kind, live.Id, 1, live.Revision);
        Error(400, () => db.Resolve(new(live.Id, 1)));
        db.Configuration["paprika:AzureEndpoint"] = "https://example.invalid";
        db.Configuration["paprika:AzureKey"] = "test-only";
        db.Configuration["paprika:JevApiKey"] = "test-only";
        Assert.Equal("approved-model", db.Resolve(new(live.Id, 1)).Model);
    }

    [Fact]
    public void LiveCatalogValidatesEachProviderModelAndQueuesOnlyWithSelectedCredentials()
    {
        using var db = new Database();
        db.Configuration["paprika:OpenAiModel"] = "gpt-approved";
        db.Configuration["paprika:JevApiKey"] = "jev-test-key";
        var catalog = db.Registry.Catalog();
        Assert.Equal(
            new[]
            {
                new ClassifierProviderInfo("jev", "approved-model", true),
                new ClassifierProviderInfo("openai", "gpt-approved", false),
            },
            catalog.ClassifierProviders
        );
        Assert.Equal("approved-model", catalog.LiveModel);
        Assert.Contains("default", catalog.ProviderProfiles);
        var live = db.Entry("workflow", "general-logistics").Workflow! with
        {
            Mode = "live",
            Model = "gpt-approved",
        };
        CatalogEntry Create(string provider, string model) =>
            db.Registry.Create(
                new(
                    "workflow",
                    null,
                    "Live",
                    "",
                    Workflow: live with
                    {
                        ProviderProfile = provider,
                        Model = model,
                    }
                )
            );
        Error(400, () => Create("jev", "gpt-approved"));
        Error(400, () => Create("openai", "approved-model"));
        Error(400, () => Create("unsupported", "approved-model"));
        var openai = Create("default", "gpt-approved");
        var jev = Create("jev", "approved-model");
        Assert.Equal("openai", openai.Workflow!.ProviderProfile);
        db.Configuration["paprika:OpenAiModel"] = "different";
        Error(400, () => db.Registry.Publish("workflow", openai.Id, 1, 1));
        db.Configuration["paprika:OpenAiModel"] = "gpt-approved";
        db.Registry.Publish("workflow", openai.Id, 1, 1);
        db.Registry.Publish("workflow", jev.Id, 1, 1);
        Assert.Contains(
            "OCR",
            Assert.Throws<WorkflowException>(() => db.Resolve(new(jev.Id, 1))).Message,
            StringComparison.OrdinalIgnoreCase
        );
        db.Configuration["paprika:AzureEndpoint"] = "https://example.invalid";
        db.Configuration["paprika:AzureKey"] = "ocr-test-key";
        var unready = db.Store.Accept(new Packet(), null, new(openai.Id, 1));
        Assert.Equal(400, unready.Code);
        Assert.Contains("openai", unready.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.Store.All());
        var jevHash = db.Resolve(new(jev.Id, 1)).Hash;
        db.Configuration["paprika:OpenAiApiKey"] = "openai-test-key";
        Assert.Equal(jevHash, db.Resolve(new(jev.Id, 1)).Hash);
        var accepted = db.Store.Accept(new Packet(), null, new(openai.Id, 1));
        Assert.Equal("openai", accepted.Packet!.Workflow!.ProviderProfile);
        Assert.Equal("gpt-approved", accepted.Packet.Workflow.Model);
    }

    [Theory]
    [InlineData("default")]
    [InlineData(null)]
    public void LegacyLiveCatalogPinsNewVersionsAndIntakeWithoutRewritingThePublication(
        string? profile
    )
    {
        using var db = new Database("live");
        db.Configuration["paprika:OpenAiApiKey"] = "openai-test-key";
        db.Configuration["paprika:OpenAiModel"] = "gpt-approved";
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE workflow_catalog SET json=json_set(json,'$.workflow.providerProfile',$profile,'$.workflow.model','gpt-approved')
            WHERE kind='workflow' AND id='general-logistics' AND version=1
            """;
        q.Parameters.AddWithValue("$profile", (object?)profile ?? DBNull.Value);
        Assert.Equal(1, q.ExecuteNonQuery());
        q.Parameters.Clear();
        q.CommandText =
            "SELECT json FROM workflow_catalog WHERE kind='workflow' AND id='general-logistics' AND version=1";
        var original = (string)q.ExecuteScalar()!;
        var old = db.Entry("workflow", "general-logistics");
        Assert.Equal(profile, old.Workflow!.ProviderProfile);
        Assert.Equal("openai", old.ResolvedClassifierProvider);
        var snapshot = db.Resolve();
        Assert.Equal("openai", snapshot.ProviderProfile);
        db.Configuration["paprika:JevApiKey"] = "jev-test-key";
        Assert.Equal(snapshot.Hash, db.Resolve().Hash);
        var clone = db.Registry.Clone(old.Kind, old.Id, 1, 1, "Clone");
        var next = db.Registry.NewVersion(old.Kind, old.Id, 1, 1);
        Assert.Equal("openai", clone.Workflow!.ProviderProfile);
        Assert.Equal("openai", next.Workflow!.ProviderProfile);
        Assert.Equal(
            "openai",
            db.Registry.Publish(next.Kind, next.Id, 2, 1).Workflow!.ProviderProfile
        );
        db.Configuration["paprika:JevModel"] = "gpt-approved";
        Assert.Null(db.Entry(old.Kind, old.Id).ResolvedClassifierProvider);
        Error(400, () => db.Resolve());
        Assert.Equal("openai", db.Resolve(new(next.Id, 2)).ProviderProfile);
        var repair = db.Registry.Clone(old.Kind, old.Id, 1, 1, "Repair ambiguous workflow");
        Assert.True(repair.ClassifierBindingNeedsRepair);
        Assert.Equal(profile, repair.Workflow!.ProviderProfile);
        Assert.Equal("gpt-approved", repair.Workflow.Model);
        // Even if the ambiguity disappears, the copied draft needs an explicit choice.
        db.Configuration["paprika:JevModel"] = "approved-model";
        Error(400, () => db.Registry.Publish(repair.Kind, repair.Id, 1, 1));
        Error(400, () =>
            db.Registry.Edit(
                repair.Kind,
                repair.Id,
                1,
                repair with
                {
                    ClassifierBindingNeedsRepair = false,
                }
            )
        );
        var repaired = db.Registry.Edit(
            repair.Kind,
            repair.Id,
            1,
            repair with
            {
                Workflow = repair.Workflow with { ProviderProfile = "openai" },
            }
        );
        Assert.False(repaired.ClassifierBindingNeedsRepair);
        db.Registry.Publish(repaired.Kind, repaired.Id, 1, repaired.Revision);
        Assert.Equal("openai", db.Resolve(new(repaired.Id, 1)).ProviderProfile);
        Assert.Equal(original, q.ExecuteScalar());
    }

    [Fact]
    public void StalePublishedModelCanBeRepairedInNewVersionWithoutRelaxingOtherValidation()
    {
        using var db = new Database("live");
        var old = db.Entry("workflow", "general-logistics");
        db.Configuration["paprika:JevModel"] = "replacement";
        var repair = db.Registry.NewVersion(old.Kind, old.Id, 1, 1);
        Assert.True(db.Entry(repair.Kind, repair.Id, repair.Version).ClassifierBindingNeedsRepair);
        Assert.Equal("jev", repair.Workflow!.ProviderProfile);
        Assert.Equal("approved-model", repair.Workflow.Model);
        Error(400, () => db.Registry.Publish(repair.Kind, repair.Id, repair.Version, 1));
        Error(400, () => db.Registry.Edit(repair.Kind, repair.Id, repair.Version, repair));
        Error(400, () => db.Registry.Clone(old.Kind, old.Id, 1, 1, ""));
        repair = db.Registry.Edit(
            repair.Kind,
            repair.Id,
            repair.Version,
            repair with
            {
                Workflow = repair.Workflow with { Model = "replacement" },
            }
        );
        var published = db.Registry.Publish(
            repair.Kind,
            repair.Id,
            repair.Version,
            repair.Revision
        );
        Assert.Equal("replacement", published.Workflow!.Model);
        Assert.False(published.ClassifierBindingNeedsRepair);
        Assert.Equal("approved-model", db.Entry(old.Kind, old.Id).Workflow!.Model);
    }

    [Fact]
    public void ResolveReadsDefaultAndDependenciesInsideSuppliedUncommittedTransaction()
    {
        using var db = new Database();
        var copy = db.Registry.Clone(
            "workflow",
            "general-logistics",
            1,
            1,
            "Transactional default"
        );
        copy = db.Registry.Publish(copy.Kind, copy.Id, 1, copy.Revision);
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using (var tx = c.BeginTransaction())
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "UPDATE workflow_settings SET json=$json WHERE key='default'";
            q.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(new VersionRef(copy.Id, 1), Contract.Json)
            );
            q.ExecuteNonQuery();
            Assert.Equal(copy.Id, db.Registry.Resolve(c, tx, new()).Id);
            tx.Rollback();
        }
        Assert.Equal("general-logistics", db.Resolve().Id);
    }

    [Fact]
    public void SnapshotsAndLegacyHashesAreDeterministicAndContainNoCredentials()
    {
        using var db = new Database();
        var first = db.Resolve();
        Assert.True(first.LegacyRules);
        Assert.Matches("^[a-f0-9]{64}$", first.Hash);
        Assert.Equal(first.Hash, db.Resolve().Hash);
        var restored = JsonSerializer.Deserialize<WorkflowSnapshot>(
            JsonSerializer.Serialize(first, Contract.Json),
            Contract.Json
        )!;
        Assert.Equal(first.Hash, restored.Hash);
        Assert.True(restored.LegacyRules);
        var copy = db.Registry.Clone("workflow", first.Id, 1, 1, "Copied");
        Assert.True(db.Resolve(new(copy.Id, 1, true, 1)).LegacyRules);
        foreach (var mode in new[] { "simulation", "demo", "live" })
        {
            var legacy = WorkflowRegistry.Legacy(mode);
            Assert.Equal(mode, legacy.Mode);
            Assert.True(legacy.LegacyRules);
            Assert.Equal(legacy.Hash, WorkflowRegistry.Legacy(mode).Hash);
            Assert.Matches("^[a-f0-9]{64}$", legacy.Hash);
        }
        db.Configuration["paprika:AzureKey"] = "secret-sentinel-azure";
        db.Configuration["paprika:JevApiKey"] = "secret-sentinel-classifier";
        var json = JsonSerializer.Serialize(
            new { Catalog = db.Registry.Catalog(), Snapshot = db.Resolve() },
            Contract.Json
        );
        Assert.DoesNotContain("secret-sentinel", json);
        Assert.Equal("approved-model", db.Registry.Catalog().LiveModel);
        Assert.Equal("approved-model", WorkflowRegistry.Legacy("live", "approved-model").Model);
        Assert.NotEqual(
            WorkflowRegistry.Legacy("live").Hash,
            WorkflowRegistry.Legacy("live", "approved-model").Hash
        );
    }

    [Fact]
    public void StatisticsCountActualCorrectionsIncludingEditedApprovalsAndExcludeTestRuns()
    {
        using var db = new Database();
        Packet Run(bool testRun) =>
            new()
            {
                Workflow = db.Resolve(),
                TestRun = testRun,
                Status = "review",
                Pages = [new() { Id = "page", PredictedType = "invoice" }],
                Documents =
                [
                    new()
                    {
                        Id = "doc",
                        Type = "invoice",
                        PageIds = ["page"],
                    },
                ],
            };
        var approved = Run(false);
        db.Store.Save(approved);
        var approvedResult = db.Store.Review(approved.Id, new(1, approved.Documents, true));
        Assert.Equal(200, approvedResult.Code);
        var corrected = Run(false);
        db.Store.Save(corrected);
        var changes = new List<Document>
        {
            new()
            {
                Id = "doc",
                Type = "receipt",
                PageIds = ["page"],
                BusinessReference = "R-123",
            },
        };
        Assert.Equal(200, db.Store.Review(corrected.Id, new(1, changes, true)).Code);
        var noOp = Run(false);
        db.Store.Save(noOp);
        Assert.Equal(200, db.Store.Review(noOp.Id, new(1, noOp.Documents, false)).Code);
        var test = Run(true);
        db.Store.Save(test);
        Assert.Equal(200, db.Store.Review(test.Id, new(1, changes, true)).Code);
        var failed = Run(false);
        failed.Status = "failed";
        db.Store.Save(failed);
        var stats = db
            .Registry.Catalog()
            .Stats.Single(s => s.Id == "general-logistics" && s.Version == 1);
        Assert.Equal(new WorkflowStats("general-logistics", 1, 4, 1, 1, 2, 1), stats);
    }

    private static void Error(int code, Action action) =>
        Assert.Equal(code, Assert.Throws<WorkflowException>(action).Code);

    private sealed class Database : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "paprika-registry-tests-" + Guid.NewGuid().ToString("N")
        );
        public IConfigurationRoot Configuration { get; }
        public Store Store { get; }
        public WorkflowRegistry Registry => Store.Workflows;

        public Database(string mode = "simulation")
        {
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["paprika:DataDirectory"] = root,
                        ["paprika:Mode"] = mode,
                        ["paprika:JevModel"] = "approved-model",
                    }
                )
                .Build();
            Store = new(Configuration);
        }

        public CatalogEntry Entry(string kind, string id, int version = 1) =>
            Registry
                .Catalog()
                .Entries.Single(e => e.Kind == kind && e.Id == id && e.Version == version);

        public WorkflowSnapshot Resolve(WorkflowSelection? selection = null)
        {
            using var c = new SqliteConnection(Store.ConnectionString);
            c.Open();
            using var tx = c.BeginTransaction();
            var snapshot = Registry.Resolve(c, tx, selection ?? new());
            tx.Commit();
            return snapshot;
        }

        public void Dispose()
        {
            using var c = new SqliteConnection(Store.ConnectionString);
            SqliteConnection.ClearPool(c);
            Directory.Delete(root, true);
        }
    }
}
