using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class CaseFlowApiTests
{
    [Fact]
    public async Task SharedReviewerCanUseScopedCaseApiWhileForeignAndArchivedMembersStayHidden()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        packet.Status = "approved";
        packet.CreatedAt = DateTimeOffset.UtcNow.AddDays(-100);
        db.Store.Save(packet);
        const string token = "synthetic-case-flow-api-token-fixture-only-12345";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder
            .Configuration.AddConfiguration(db.Configuration)
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:Operations:Mode"] = "shared",
                    ["paprika:Operations:Users:0:Name"] = "alice",
                    ["paprika:Operations:Users:0:Roles:0"] = "reviewer",
                    ["paprika:Operations:Users:0:Workflows:0"] = packet.EffectiveWorkflow.Id,
                    ["paprika:Operations:Users:0:Token"] = token,
                    ["paprika:Operations:Retention:ExecutionEnabled"] = "true",
                }
            );
        builder.Services.AddSingleton(db.Store);
        builder.Services.AddOperationsPlatform(builder.Configuration);
        builder.Services.AddSingleton<CaseStore>();
        builder.Services.AddSingleton<FlowEngine>();
        builder.Services.AddSingleton<FlowLibrary>();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseOperationsPlatform();
        app.MapCaseEndpoints();
        app.MapFlowEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var created = await client.PostAsJsonAsync(
            "/api/cases",
            new CaseCreateRequest(packet.Id, packet.Revision, "S123", [packet.Documents[0].Id])
        );
        created.EnsureSuccessStatusCode();
        var record = (await created.Content.ReadFromJsonAsync<CaseRecord>(Contract.Json))!;
        Assert.Equal("alice", Assert.Single(record.Audit).Actor);
        using var list = await client.GetAsync("/api/cases");
        list.EnsureSuccessStatusCode();
        Assert.Single((await list.Content.ReadFromJsonAsync<CaseRecord[]>(Contract.Json))!);

        var foreign = db.Packet("pod");
        foreign.Workflow = foreign.EffectiveWorkflow with { Id = "foreign-workflow" };
        db.Store.Save(foreign);
        var foreignCase = new CaseStore(db.Store).Create(
            new(foreign.Id, 1, "PRIVATE", [foreign.Documents[0].Id]),
            "setup",
            _ => true
        );
        using var denied = await client.GetAsync($"/api/cases/{foreignCase.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var deniedWrite = await client.PostAsJsonAsync(
            $"/api/cases/{record.Id}/members",
            new CaseMembershipRequest(record.Revision, foreign.Id, 1, [foreign.Documents[0].Id])
        );
        Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        using var library = await client.GetAsync("/api/flows/library");
        Assert.Equal(HttpStatusCode.Forbidden, library.StatusCode);
        using var packetFlow = await client.GetAsync($"/api/flows/{packet.Id}");
        packetFlow.EnsureSuccessStatusCode();

        // This deliberately minimal host omits retention participants, permitting an archive to exercise route-level fail-closed behavior.
        var operations = app.Services.GetRequiredService<OperationsPlatformStore>();
        operations.Archive(
            new(packet.Id, packet.Revision, true),
            new("administrator", ["administrator"], [])
        );
        using var archived = await client.GetAsync($"/api/cases/{record.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, archived.StatusCode);
        using var afterArchive = await client.GetAsync("/api/cases");
        afterArchive.EnsureSuccessStatusCode();
        Assert.Empty((await afterArchive.Content.ReadFromJsonAsync<CaseRecord[]>(Contract.Json))!);
        using var archivedEdit = await client.PutAsJsonAsync(
            $"/api/cases/{record.Id}",
            new CaseEditRequest(record.Revision, ["bol"])
        );
        Assert.Equal(HttpStatusCode.NotFound, archivedEdit.StatusCode);
    }
}
