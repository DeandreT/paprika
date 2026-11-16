using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class OperationsPlatformTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "operations-tests",
        "data-" + Guid.NewGuid().ToString("N")
    );
    private static readonly OperationsIdentity Admin = new("admin", ["administrator"], []);
    private const string Token = "synthetic-test-token-not-a-real-secret-123456";

    private IConfiguration Config(
        bool shared = false,
        bool execution = false,
        bool purge = false
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:Operations:Mode"] = shared ? "shared" : "local",
                    ["paprika:Operations:UseAspNetIdentity"] = "true",
                    ["paprika:Operations:Users:0:Name"] = "alice",
                    ["paprika:Operations:Users:0:Roles:0"] = "reviewer",
                    ["paprika:Operations:Users:0:Workflows:0"] = "legacy-logistics",
                    ["paprika:Operations:Users:0:Token"] = Token,
                    ["paprika:Operations:Retention:ExecutionEnabled"] = execution.ToString(),
                    ["paprika:Operations:Retention:PermanentDeletionEnabled"] = purge.ToString(),
                }
            )
            .Build();

    private Packet Packet(Store store, string status = "review", int age = 0)
    {
        var p = new Packet { Status = status, CreatedAt = DateTimeOffset.UtcNow.AddDays(-age) };
        store.Save(p);
        return p;
    }

    private static OperationsIdentity Reviewer(Packet p, string name = "alice") =>
        new(name, ["reviewer"], [p.EffectiveWorkflow.Id]);

    private OperationsAccess Access(IConfiguration config) =>
        new(config, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));

    [Fact]
    public void SharedAuthenticationFailsClosedAndIgnoresCallerRoles()
    {
        var access = Access(Config(shared: true));
        var context = new DefaultHttpContext();
        context.Request.Headers["X-User"] = "alice";
        context.Request.Headers["X-Role"] = "administrator";
        Assert.Null(access.Resolve(context));
        context.Request.Headers.Authorization = "Bearer wrong";
        Assert.Null(access.Resolve(context));
        context.Request.Headers.Authorization = "Bearer " + Token;
        var identity = Assert.IsType<OperationsIdentity>(access.Resolve(context));
        Assert.Equal("alice", identity.Name);
        Assert.False(identity.Administrator);
        Assert.False(identity.CanRead("foreign-workflow"));
        Assert.False(new OperationsIdentity("unprivileged", [], ["allowed"]).CanRead("allowed"));
    }

    [Fact]
    public void AspNetIdentityRequiresExplicitServerAllowlist()
    {
        var access = Access(Config(shared: true));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "mallory"),
                        new Claim(ClaimTypes.Role, "administrator"),
                    ],
                    "test"
                )
            ),
        };
        Assert.Null(access.Resolve(context));
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.Role, "administrator")],
                "test"
            )
        );
        Assert.False(access.Resolve(context)!.Administrator);
    }

    [Fact]
    public void LocalModeRemainsUsableWithoutCredentials()
    {
        Assert.True(Access(Config()).Resolve(new DefaultHttpContext())!.Administrator);
    }

    [Fact]
    public void SessionCookieIsHttpOnlyAndDoesNotContainConfiguredCredential()
    {
        var access = Access(Config(shared: true));
        var context = new DefaultHttpContext();
        access.SignIn(context, new("alice", ["reviewer"], []));
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.DoesNotContain(Token, cookie);
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = cookie.Split(';')[0];
        Assert.Equal("alice", access.Resolve(next)!.Name);
    }

    [Fact]
    public async Task CompetingClaimsHaveExactlyOneWinnerAndPersistAcrossStores()
    {
        var config = Config();
        var store = new Store(config);
        var packet = Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var task = Assert.Single(operations.Tasks(Admin));
        var attempts = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(index =>
                    Task.Run(() =>
                    {
                        try
                        {
                            new OperationsPlatformStore(new Store(config), config).Change(
                                task.Id,
                                new(task.Revision),
                                Reviewer(packet, "actor-" + index),
                                "claim"
                            );
                            return true;
                        }
                        catch (OperationsConflict)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Equal(1, attempts.Count(x => x));
        Assert.Equal(
            "claimed",
            Assert.Single(new OperationsPlatformStore(new Store(config), config).Tasks(Admin)).State
        );
    }

    [Fact]
    public void LeaseExpiryRequiresReloadAndDisallowsOldOwner()
    {
        var config = Config();
        var store = new Store(config);
        var packet = Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var now = DateTimeOffset.UtcNow;
        var task = Assert.Single(operations.Tasks(Admin, now));
        task = operations.Change(task.Id, new(task.Revision, 30), Reviewer(packet), "claim", now);
        Assert.Throws<OperationsConflict>(() =>
            operations.Change(
                task.Id,
                new(task.Revision),
                Reviewer(packet),
                "renew",
                now.AddSeconds(31)
            )
        );
        var expired = Assert.Single(operations.Tasks(Admin, now.AddSeconds(31)));
        Assert.Equal("available", expired.State);
        Assert.True(expired.Revision > task.Revision);
        var claimed = operations.Change(
            expired.Id,
            new(expired.Revision),
            Reviewer(packet, "bob"),
            "claim",
            now.AddSeconds(31)
        );
        Assert.Equal("bob", claimed.ClaimedBy);
        Assert.Contains(claimed.History, e => e.Action == "lease expired");
    }

    [Fact]
    public void StaleRevisionForeignWorkflowAndForeignOwnerCannotMutateTask()
    {
        var config = Config();
        var store = new Store(config);
        var packet = Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var task = Assert.Single(operations.Tasks(Admin));
        Assert.Empty(operations.Tasks(new("stranger", ["reviewer"], ["elsewhere"])));
        Assert.Equal(
            404,
            Assert
                .Throws<OperationsConflict>(() =>
                    operations.Change(
                        task.Id,
                        new(task.Revision),
                        new("stranger", ["reviewer"], []),
                        "claim"
                    )
                )
                .StatusCode
        );
        var claimed = operations.Change(task.Id, new(task.Revision), Reviewer(packet), "claim");
        Assert.Throws<OperationsConflict>(() =>
            operations.Change(task.Id, new(task.Revision), Reviewer(packet), "release")
        );
        Assert.Throws<OperationsConflict>(() =>
            operations.Change(task.Id, new(claimed.Revision), Reviewer(packet, "bob"), "release")
        );
    }

    [Fact]
    public void SlaOrderingUsesPriorityThenDueDate()
    {
        var config = Config();
        var store = new Store(config);
        Packet(store);
        Packet(store);
        Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var tasks = operations.Tasks(Admin);
        var due = DateTimeOffset.UtcNow;
        operations.Schedule(tasks[0].Id, new(tasks[0].Revision, 5, due.AddDays(2)), Admin);
        operations.Schedule(tasks[1].Id, new(tasks[1].Revision, 5, due.AddDays(1)), Admin);
        operations.Schedule(tasks[2].Id, new(tasks[2].Revision, 1, due.AddDays(-1)), Admin);
        Assert.Equal(
            new[] { tasks[1].Id, tasks[0].Id, tasks[2].Id },
            operations.Tasks(Admin).Select(t => t.Id)
        );
    }

    [Fact]
    public void ChangedPacketRevisionSupersedesOldTask()
    {
        var config = Config();
        var store = new Store(config);
        var packet = Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var original = Assert.Single(operations.Tasks(Admin));
        packet.Revision++;
        store.Save(packet);
        var tasks = operations.Tasks(Admin);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("superseded", tasks.Single(t => t.Id == original.Id).State);
        Assert.Equal(2, tasks.Single(t => t.State == "available").PacketRevision);
    }

    [Fact]
    public void SharedTransactionalReviewGuardRequiresCurrentLeaseOwner()
    {
        var config = Config(shared: true);
        var store = new Store(config);
        var packet = Packet(store);
        var operations = new OperationsPlatformStore(store, config);
        var task = Assert.Single(operations.Tasks(Admin));
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(store.ConnectionString);
        c.Open();
        using (var tx = c.BeginTransaction())
        {
            Assert.NotNull(operations.ValidateReview(c, tx, packet, "alice"));
        }
        operations.Change(task.Id, new(task.Revision), Reviewer(packet), "claim");
        using (var tx = c.BeginTransaction())
        {
            Assert.Null(operations.ValidateReview(c, tx, packet, "alice"));
            Assert.NotNull(operations.ValidateReview(c, tx, packet, "bob"));
            operations.RecordReview(c, tx, packet.Id, packet.Revision, "alice", "approved");
            tx.Commit();
        }
        Assert.Equal("approved", Assert.Single(operations.Tasks(Admin)).Outcome);
    }

    [Fact]
    public void MutationGuardAllowsRecoveryButRespectsAnotherActiveApprovedCorrectionClaim()
    {
        var config = Config(shared: true);
        var store = new Store(config);
        var packet = Packet(store, "failed");
        var operations = new OperationsPlatformStore(store, config);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(store.ConnectionString);
        c.Open();
        using (var tx = c.BeginTransaction())
            Assert.Null(operations.ValidateMutation(c, tx, packet, "alice"));
        packet.Status = "approved";
        store.Save(packet);
        var task = operations.OpenReview(packet.Id, packet.Revision, Reviewer(packet));
        operations.Change(task.Id, new(task.Revision), Reviewer(packet), "claim");
        using (var tx = c.BeginTransaction())
        {
            Assert.Null(operations.ValidateMutation(c, tx, packet, "alice"));
            Assert.NotNull(operations.ValidateMutation(c, tx, packet, "bob"));
        }
    }

    [Fact]
    public void RetentionProtectsActiveRecentAndParentPackets()
    {
        var config = Config(execution: true);
        var store = new Store(config);
        var active = Packet(store, "processing", 120);
        var recent = Packet(store, "approved");
        var parent = Packet(store, "approved", 120);
        var child = Packet(store, "approved", 120);
        child.ParentPacketId = parent.Id;
        store.Save(child);
        var operations = new OperationsPlatformStore(store, config);
        var preview = operations.Preview();
        Assert.False(preview.Single(p => p.PacketId == active.Id).Eligible);
        Assert.False(preview.Single(p => p.PacketId == recent.Id).Eligible);
        Assert.False(preview.Single(p => p.PacketId == parent.Id).Eligible);
        Assert.Throws<OperationsConflict>(() =>
            operations.Archive(new(active.Id, active.Revision, true), Admin)
        );
    }

    [Fact]
    public void RetentionRequiresOptInConfirmationRevisionAndAdministrator()
    {
        var config = Config();
        var store = new Store(config);
        var packet = Packet(store, "approved", 120);
        var operations = new OperationsPlatformStore(store, config);
        Assert.Throws<OperationsConflict>(() =>
            operations.Archive(new(packet.Id, packet.Revision, true), Admin)
        );
        operations = new OperationsPlatformStore(store, Config(execution: true));
        Assert.Throws<OperationsConflict>(() =>
            operations.Archive(new(packet.Id, packet.Revision, false), Admin)
        );
        Assert.Throws<OperationsConflict>(() =>
            operations.Archive(new(packet.Id, packet.Revision + 1, true), Admin)
        );
        Assert.Equal(
            403,
            Assert
                .Throws<OperationsConflict>(() =>
                    operations.Archive(new(packet.Id, packet.Revision, true), Reviewer(packet))
                )
                .StatusCode
        );
        operations.Archive(new(packet.Id, packet.Revision, true), Admin);
        Assert.NotNull(store.Get(packet.Id));
        Assert.True(operations.IsArchived(packet.Id));
        Assert.Single(operations.Tombstones());
    }

    [Fact]
    public void ExplicitPurgeDeletesOnlyArchivedDependencyFreeOriginalsAndKeepsAudit()
    {
        var config = Config(execution: true, purge: true);
        var store = new Store(config);
        var packet = Packet(store, "approved", 120);
        var directory = Path.Combine(root, packet.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "original.pdf"), "synthetic fixture");
        var operations = new OperationsPlatformStore(store, config);
        Assert.False(operations.PurgePlan(packet.Id).Eligible);
        operations.Archive(new(packet.Id, packet.Revision, true), Admin);
        Assert.True(operations.PurgePlan(packet.Id).Eligible);
        operations.Purge(new(packet.Id, packet.Revision, true), Admin);
        Assert.Null(store.Get(packet.Id));
        Assert.False(Directory.Exists(directory));
        Assert.Contains("permanently deleted", Assert.Single(operations.Tombstones()).Action);
    }

    [Fact]
    public void UnknownModuleTablesAndSavedHistoryBlockPhysicalDeletion()
    {
        var config = Config(execution: true, purge: true);
        var store = new Store(config);
        var packet = Packet(store, "approved", 120);
        var operations = new OperationsPlatformStore(store, config);
        operations.Archive(new(packet.Id, packet.Revision, true), Admin);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE future_dependencies(id TEXT); INSERT INTO revisions VALUES($id,1,'{}')";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.ExecuteNonQuery();
        var plan = operations.PurgePlan(packet.Id);
        Assert.False(plan.Eligible);
        Assert.Contains(plan.Blockers, b => b.Contains("future_dependencies"));
        Assert.Contains(plan.Blockers, b => b.Contains("history"));
        Assert.Throws<OperationsConflict>(() =>
            operations.Purge(new(packet.Id, packet.Revision, true), Admin)
        );
    }

    [Fact]
    public void InProgressDeliveryBlocksArchiveAndAnyDeliverySnapshotBlocksPurge()
    {
        var config = Config(execution: true, purge: true);
        var store = new Store(config);
        var packet = Packet(store, "approved", 120);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE deliveries(id TEXT PRIMARY KEY,packet_id TEXT,response TEXT); INSERT INTO deliveries VALUES('delivery',$id,'{\"status\":\"processing\"}')";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.ExecuteNonQuery();
        var operations = new OperationsPlatformStore(store, config);
        Assert.Contains(Assert.Single(operations.Preview()).Reasons, r =>
            r.Contains("in-progress delivery")
        );
        Assert.Throws<OperationsConflict>(() =>
            operations.Archive(new(packet.Id, packet.Revision, true), Admin)
        );
        q.CommandText =
            "UPDATE deliveries SET response='{\"status\":\"delivered\"}' WHERE packet_id=$id";
        q.ExecuteNonQuery();
        operations.Archive(new(packet.Id, packet.Revision, true), Admin);
        Assert.Contains(operations.PurgePlan(packet.Id).Blockers, r =>
            r.Contains("Delivery snapshots")
        );
    }

    [Theory]
    [InlineData("/api/messaging", 403)]
    [InlineData("/api/operations", 403)]
    [InlineData("/api/workflows", 403)]
    [InlineData("/api/deliveries/private/artifact", 403)]
    [InlineData("/api/quality", 403)]
    [InlineData("/api/flows", 403)]
    public async Task SharedLegacyAndFutureGlobalApisCannotBypassAuthorization(
        string path,
        int expected
    )
    {
        var config = Config(shared: true);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(config);
        services.AddSingleton<Store>();
        services.AddOperationsPlatform(config);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseOperationsPlatform();
        app.Run(c =>
        {
            c.Response.StatusCode = 299;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer " + Token;
        context.Response.Body = new MemoryStream();
        await app.Build()(context);
        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task PacketScopedModuleMetadataAllowsReviewerButRejectsForeignAndAmbiguousTargets()
    {
        var config = Config(shared: true);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(config);
        services.AddSingleton<Store>();
        services.AddOperationsPlatform(config);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<Store>();
        var allowed = Packet(store);
        var foreign = Packet(store);
        foreign.Workflow = WorkflowRegistry.Legacy() with { Id = "foreign" };
        store.Save(foreign);
        var app = new ApplicationBuilder(provider);
        app.UseOperationsPlatform();
        app.Run(c =>
        {
            c.Response.StatusCode = 299;
            return Task.CompletedTask;
        });
        async Task<int> Call(string query)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Method = "GET";
            context.Request.Path = "/api/extraction/results";
            context.Request.QueryString = new(query);
            context.Request.Headers.Authorization = "Bearer " + Token;
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(
                new Endpoint(
                    _ => Task.CompletedTask,
                    new EndpointMetadataCollection(new OperationsPacketPermission()),
                    "module-read"
                )
            );
            await app.Build()(context);
            return context.Response.StatusCode;
        }
        Assert.Equal(299, await Call("?packetId=" + allowed.Id));
        Assert.Equal(404, await Call("?packetId=" + foreign.Id));
        Assert.Equal(404, await Call("?packetId=" + allowed.Id + "&packetId=" + foreign.Id));
        Assert.Equal(404, await Call(""));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
