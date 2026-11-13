using Microsoft.Extensions.Configuration;
using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class CaseStoreTests
{
    [Fact]
    public void SourceRevisionRefreshPreservesPriorMembershipProvenanceInHistory()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var cases = new CaseStore(db.Store);
        var record = cases.Create(new(packet.Id, 1, "S123", [packet.Documents[0].Id]), "alice", _ =>
            true
        );
        packet.Revision = 2;
        packet.Documents[0].Type = "pod";
        db.Store.Save(packet);
        var refreshed = cases.AddMembers(
            record.Id,
            new(record.Revision, packet.Id, 2, [packet.Documents[0].Id], true),
            "alice",
            _ => true
        );
        Assert.Equal("pod", Assert.Single(refreshed.Members).DocumentType);
        Assert.Equal(2, Assert.Single(refreshed.Members).PacketRevision);
        Assert.Equal(
            "bol",
            Assert.Single(Assert.Single(cases.History(record.Id)).Members).DocumentType
        );
    }

    [Fact]
    public void DistinctSubmissionsPersistMembershipCompletenessAndProvenance()
    {
        using var db = new CaseFlowDatabase();
        var first = db.Packet("bol");
        var second = db.Packet("pod");
        var cases = new CaseStore(db.Store);
        var record = cases.Create(new(first.Id, 1, "S123", [first.Documents[0].Id]), "alice", _ =>
            true
        );
        Assert.False(record.Complete);
        record = cases.AddMembers(
            record.Id,
            new(1, second.Id, 1, [second.Documents[0].Id]),
            "bob",
            _ => true
        );
        Assert.True(record.Complete);
        Assert.Equal(2, record.Members.Select(m => m.PacketId).Distinct().Count());
        var reopened = new CaseStore(new Store(db.Configuration)).Get(record.Id);
        Assert.Equal(2, reopened.Members.Length);
        Assert.All(reopened.Members, m => Assert.Equal("sha256", Assert.Single(m.Sources).Sha256));
        Assert.Equal("bob", reopened.Audit.Last().Actor);
    }

    [Fact]
    public async Task ConcurrentMembershipChecksCaseRevisionAndUniqueDocumentOwnership()
    {
        using var db = new CaseFlowDatabase();
        var initial = db.Packet("bol");
        var next = db.Packet("pod");
        var cases = new CaseStore(db.Store);
        var record = cases.Create(
            new(initial.Id, 1, "S123", [initial.Documents[0].Id]),
            "alice",
            _ => true
        );
        async Task<int> Add() =>
            await Task.Run(() =>
            {
                try
                {
                    new CaseStore(db.Store).AddMembers(
                        record.Id,
                        new(1, next.Id, 1, [next.Documents[0].Id]),
                        "reviewer",
                        _ => true
                    );
                    return 200;
                }
                catch (WorkflowException ex)
                {
                    return ex.Code;
                }
            });
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(Add(), Add())).Order().ToArray());
        Assert.Equal(2, cases.Get(record.Id).Members.Length);
        Assert.Equal(
            409,
            Assert
                .Throws<WorkflowException>(() =>
                    cases.Create(new(next.Id, 1, "OTHER", [next.Documents[0].Id]), "alice", _ =>
                        true
                    )
                )
                .Code
        );
    }

    [Fact]
    public void MembershipRejectsStalePacketCustomerScopeAndDeniedWorkflow()
    {
        using var db = new CaseFlowDatabase();
        var first = db.Packet("bol");
        var other = db.Packet("pod", "other");
        var cases = new CaseStore(db.Store);
        var record = cases.Create(new(first.Id, 1, "S123", [first.Documents[0].Id]), "alice", _ =>
            true
        );
        Assert.Equal(
            409,
            Assert
                .Throws<WorkflowException>(() =>
                    cases.AddMembers(
                        record.Id,
                        new(1, other.Id, 1, [other.Documents[0].Id]),
                        "alice",
                        _ => true
                    )
                )
                .Code
        );
        Assert.Equal(
            403,
            Assert
                .Throws<WorkflowException>(() =>
                    cases.Edit(record.Id, new(1, []), "mallory", _ => false)
                )
                .Code
        );
        Assert.Equal(
            409,
            Assert
                .Throws<WorkflowException>(() =>
                    cases.Create(new(other.Id, 2, "S123", [other.Documents[0].Id]), "alice", _ =>
                        true
                    )
                )
                .Code
        );
    }

    [Fact]
    public void AutoCollationRequiresTrustedVerifiedReferenceAndCompatibleScope()
    {
        using var db = new CaseFlowDatabase();
        var cases = new CaseStore(db.Store);
        var first = db.Packet("bol");
        var second = db.Packet("pod");
        Assert.False(cases.Collate(first, null).AutoCollated);
        Assert.Empty(cases.List());
        var accepted = cases.Collate(
            first,
            new("S123", "registered reference fixture lookup: verified")
        );
        Assert.True(accepted.AutoCollated);
        Assert.True(
            cases
                .Collate(second, new("S123", "registered reference fixture lookup: verified"))
                .AutoCollated
        );
        var record = Assert.Single(cases.List());
        Assert.True(record.Complete);
        Assert.True(cases.Collate(second, new("S123", "verified")).AutoCollated);
        Assert.Equal(2, cases.Get(record.Id).Members.Length);
        var other = db.Packet("pod", "other");
        cases.Collate(other, new("S123", "verified"));
        Assert.Equal(2, cases.List().Length);
    }

    [Fact]
    public void UnverifiedExistingCaseRemainsAProposal()
    {
        using var db = new CaseFlowDatabase();
        var cases = new CaseStore(db.Store);
        var first = db.Packet("bol");
        var second = db.Packet("pod");
        cases.Create(new(first.Id, 1, "S123", [first.Documents[0].Id]), "alice", _ => true);
        Assert.False(cases.Collate(second, new("S123", "verified")).AutoCollated);
        Assert.Single(Assert.Single(cases.List()).Members);
    }
}

internal sealed class CaseFlowDatabase : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "paperwork-case-flow-" + Guid.NewGuid().ToString("N")
    );
    public IConfiguration Configuration { get; }
    public Store Store { get; }

    public CaseFlowDatabase()
    {
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Paperwork:DataDirectory"] = root }
            )
            .Build();
        Store = new Store(Configuration);
    }

    public Packet Packet(string type = "bol", string customer = "customer")
    {
        var packet = new Packet
        {
            Status = "review",
            Workflow = WorkflowRegistry.Legacy() with
            {
                Capabilities = new(Case: new(customer, ["bol", "pod"], true)),
            },
        };
        var source = new Source { Name = "source.pdf", Sha256 = "sha256" };
        packet.Sources.Add(source);
        var page = new Page { SourceId = source.Id, SourceName = source.Name };
        packet.Pages.Add(page);
        packet.Documents.Add(
            new()
            {
                Type = type,
                PageIds = [page.Id],
                BusinessReference = "S123",
            }
        );
        Store.Save(packet);
        return packet;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
