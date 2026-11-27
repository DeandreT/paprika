using System.Diagnostics;
using System.Text.Json;

namespace paprika;

public sealed class PacketProcessor(
    Store store,
    Providers providers,
    JobCoordinator jobs,
    PlatformPipeline? platform = null,
    QualityStore? quality = null
) : IDisposable
{
    // Also protect direct calls; durable queues themselves have concurrency one.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task Process(string packetId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var packet = store.Get(packetId);
            if (packet is null || packet.Status is not ("queued" or "processing"))
                return;
            await ProcessPending(packet, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessPending(Packet packet, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var job = jobs.Begin(packet.Id, ct);
        var token = job.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            packet.Status = "processing";
            packet.Error = null;
            packet.Processing = new("reading", packet.Processing?.Model);
            store.Save(packet);
            if (packet.Mode == "live")
            {
                // Stable IDs and OCR artifacts let a restarted job resume without another OCR submission.
                foreach (var source in packet.Sources)
                {
                    token.ThrowIfCancellationRequested();
                    var texts = await providers.Ocr(packet, source, token);
                    for (int i = 0; i < texts.Count; i++)
                    {
                        var page = packet.Pages.SingleOrDefault(p =>
                            p.SourceId == source.Id && p.PageNumber == i + 1
                        );
                        if (page is null)
                        {
                            page = Worker.MakePage(packet, source, i + 1, texts[i]);
                            packet.Pages.Add(page);
                        }
                        else
                            page.Text = texts[i];
                    }
                    store.Save(packet);
                }
                Page? previous = null;
                foreach (var page in packet.Pages)
                {
                    if (page.ReviewReasons.Count == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var applied = await providers.ClassifyPage(
                            packet,
                            page,
                            previous,
                            token
                        );
                        store.Save(packet, applied);
                    }
                    previous = page;
                }
            }
            if (packet.Mode == "simulation")
                Simulation.Process(packet, store, token);
            token.ThrowIfCancellationRequested();
            if (platform is not null)
                await platform.BeforeAssemblyAsync(packet, token);
            var checkpointDocuments = packet.Documents;
            packet.Documents = WorkflowAssembly.Assemble(packet);
            if (packet.EffectiveWorkflow.Capabilities is not null)
            {
                foreach (var document in packet.Documents)
                {
                    document.Id = Convert
                        .ToHexString(
                            System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(
                                    packet.Id
                                        + "\n"
                                        + document.Type
                                        + "\n"
                                        + string.Join("\n", document.PageIds)
                                )
                            )
                        )
                        .ToLowerInvariant()[..32];
                    var checkpoint = checkpointDocuments.SingleOrDefault(d =>
                        d.Id == document.Id
                        && d.Type == document.Type
                        && d.PageIds.SequenceEqual(document.PageIds)
                    );
                    if (checkpoint is not null)
                        document.BusinessReference = checkpoint.BusinessReference;
                }
            }
            packet.IdentifierCandidates = Identifiers
                .Extract(packet.Pages)
                .Concat(
                    packet.SourceIdentifiers.Select(s => new IdentifierCandidate(
                        s.Kind,
                        s.Value,
                        packet
                            .Pages.Where(p => p.SourceId == s.SourceId)
                            .Select(p => p.Id)
                            .ToList(),
                        s.Evidence
                    ))
                )
                .Where(c => packet.EffectiveWorkflow.IdentifierKinds.Contains(c.Kind))
                .GroupBy(c => (c.Kind, c.Value))
                .Select(g =>
                    g.First() with
                    {
                        PageIds = g.SelectMany(c => c.PageIds).Distinct().ToList(),
                    }
                )
                .ToList();
            WorkflowAssembly.RequiredIdentifiers(packet);
            // Persist stable assembly before resumable module/block work.
            store.Save(packet);
            var decision = platform is null
                ? null
                : await platform.AfterAssemblyAsync(packet, token);
            packet.Status = "review";
            packet.Processing = new(
                "complete",
                packet.Processing?.Model,
                watch.ElapsedMilliseconds
            );
            packet.Audit.Add(
                new(DateTimeOffset.UtcNow, "processed; manual review required", packet.Revision)
            );
            var prediction = JsonSerializer.Serialize(packet, Contract.Json);
            if (decision?.AutoApprove == true)
            {
                packet.Status = "approved";
                packet.Audit.Add(
                    new(
                        DateTimeOffset.UtcNow,
                        "approved by validated automation policy",
                        packet.Revision,
                        "automation"
                    )
                );
            }
            store.CompleteProcessing(packet, prediction, quality, decision);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave the latest checkpoint pending for the next host, rather than mark failed.
            throw;
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested || store.Get(packet.Id)?.Status == "cancelled") { }
        finally
        {
            jobs.End(packet.Id);
            await job.CancelAsync();
        }
    }

    public void Dispose() => gate.Dispose();
}
