using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class ReferencePlatformTests : IDisposable
{
    private readonly ConnectorTestFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public async Task ImmutableVersionsPinHashAndStaleVerificationIsRejected()
    {
        var service = fixture.References;
        var first = service.Create(
            new(
                "orders",
                "Synthetic orders",
                true,
                [new("ORDER-1", "record-1", "Synthetic record")]
            ),
            "reviewer"
        );
        var second = service.Create(
            new("orders", "Synthetic orders", true, [new("ORDER-2", "record-2", "Another record")])
        );
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.NotEqual(first.Hash, second.Hash);
        var packet = fixture.Ready(new("local", first.Id, first.Version, first.Hash));
        packet.Documents[0].BusinessReference = "ORDER-1";
        fixture.Store.Save(packet);
        var result = (await service.LookupAsync(new(packet.Id, packet.Revision), default))[0];
        Assert.Equal("candidate", result.Status);
        Assert.True(result.Synthetic);
        Assert.Equal(first.Hash, result.DatasetHash);
        var verified = service.Verify(
            new(packet.Id, packet.Revision, result.DocumentId, result.ResultHash, "record-1"),
            "reviewer"
        );
        Assert.Equal("verified", verified.Status);
        Assert.Equal("reviewer", verified.VerifiedBy);
        packet.Revision++;
        fixture.Store.Save(packet);
        Assert.False(service.IsVerified(packet));
        Assert.Throws<InvalidOperationException>(() =>
            service.Verify(
                new(packet.Id, result.Revision, result.DocumentId, result.ResultHash, "record-1")
            )
        );
        using var c = new SqliteConnection(fixture.Store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        Assert.Null(
            ReferencePolicies.Validate(c, tx, new("local", first.Id, first.Version, first.Hash))
        );
        Assert.NotNull(
            ReferencePolicies.Validate(c, tx, new("local", first.Id, first.Version, second.Hash))
        );
    }

    [Fact]
    public async Task ReviewCarriesOnlyUnchangedVerification()
    {
        var data = fixture.References.Create(
            new("orders", "Orders", false, [new("ORDER-1", "one", "Order one")])
        );
        var packet = fixture.Ready(new("local", data.Id, data.Version, data.Hash));
        packet.Documents[0].BusinessReference = "ORDER-1";
        fixture.Store.Save(packet);
        var result = (
            await fixture.References.LookupAsync(new(packet.Id, packet.Revision), default)
        )[0];
        fixture.References.Verify(
            new(packet.Id, packet.Revision, result.DocumentId, result.ResultHash, "one")
        );
        var after = System.Text.Json.JsonSerializer.Deserialize<Packet>(
            System.Text.Json.JsonSerializer.Serialize(packet, Contract.Json),
            Contract.Json
        )!;
        after.Revision++;
        after.Status = "approved";
        using (var c = new SqliteConnection(fixture.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            ReferenceService.OnReview(c, tx, packet, after, "approver");
            tx.Commit();
        }
        Assert.True(fixture.References.IsVerified(after));
        var edited = System.Text.Json.JsonSerializer.Deserialize<Packet>(
            System.Text.Json.JsonSerializer.Serialize(after, Contract.Json),
            Contract.Json
        )!;
        edited.Revision++;
        edited.Documents[0].Type = "invoice";
        using (var c = new SqliteConnection(fixture.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            ReferenceService.OnReview(c, tx, after, edited, "reviewer");
            tx.Commit();
        }
        Assert.False(fixture.References.IsVerified(edited));
    }

    [Fact]
    public async Task ExtractedCandidateMatchesButSyntheticNeverAutomaticallyVerifies()
    {
        var data = fixture.References.Create(
            new("sample", "Synthetic", true, [new("ORDER-1", "one", "Synthetic")])
        );
        var packet = fixture.Ready(
            new("local", data.Id, data.Version, data.Hash, AutoVerify: true)
        );
        packet.IdentifierCandidates =
        [
            new("order", "ORDER-1", packet.Documents[0].PageIds, "synthetic text"),
        ];
        packet.Status = "processing";
        fixture.Store.Save(packet);
        await fixture.References.ProcessAsync(packet, default);
        Assert.Equal("ORDER-1", packet.Documents[0].BusinessReference);
        var result = Assert.Single(fixture.References.Results(packet.Id));
        Assert.Equal("candidate", result.Status);
        Assert.True(result.Synthetic);
        Assert.False(fixture.References.IsVerified(packet));
    }

    [Theory]
    [InlineData("missing", "notFound")]
    [InlineData("DUP", "ambiguous")]
    public async Task MissingAndDuplicateReferencesAreNotVerified(string reference, string status)
    {
        var data = fixture.References.Create(
            new("data", "Data", false, [new("DUP", "one", "One"), new("DUP", "two", "Two")])
        );
        var packet = fixture.Ready(new("local", data.Id, data.Version, data.Hash));
        packet.Documents[0].BusinessReference = reference;
        fixture.Store.Save(packet);
        Assert.Equal(
            status,
            Assert
                .Single(
                    await fixture.References.LookupAsync(new(packet.Id, packet.Revision), default)
                )
                .Status
        );
        Assert.False(fixture.References.IsVerified(packet));
    }

    [Fact]
    public async Task HttpLookupUsesConfiguredEndpointAndRejectsRedirectAndOversize()
    {
        var call = 0;
        await using var server = new ConnectorHttpDouble(async context =>
        {
            Interlocked.Increment(ref call);
            if (call == 1)
            {
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(
                    "[{\"reference\":\"ORDER-1\",\"recordId\":\"remote-one\",\"label\":\"Remote record\"}]"u8.ToArray()
                );
            }
            else if (call == 2)
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = "http://127.0.0.1:1/never";
            }
            else
            {
                context.Response.ContentLength64 = 300000;
                await context.Response.OutputStream.WriteAsync(new byte[300000]);
            }
        });
        fixture.SetConnector("lookup", "httpLookup", endpoint: server.Url);
        var packet = fixture.Ready(
            new(
                "http",
                ConnectorId: "lookup",
                ConnectorBindingHash: fixture.Catalog.BindingHash("lookup")
            )
        );
        packet.Documents[0].BusinessReference = "ORDER-1";
        fixture.Store.Save(packet);
        Assert.Equal(
            "candidate",
            Assert
                .Single(
                    await fixture.References.LookupAsync(new(packet.Id, packet.Revision), default)
                )
                .Status
        );
        Assert.Equal(
            "unavailable",
            Assert
                .Single(
                    await fixture.References.LookupAsync(new(packet.Id, packet.Revision), default)
                )
                .Status
        );
        Assert.Equal(
            "unavailable",
            Assert
                .Single(
                    await fixture.References.LookupAsync(new(packet.Id, packet.Revision), default)
                )
                .Status
        );
        Assert.Equal(3, call);
    }
}
