using System.Security.Cryptography;

namespace paprika;

public sealed class ConnectorIntake(Store store, ConnectorCatalog catalog)
{
    public async Task<ConnectorImportResult[]> ImportAsync(
        string id,
        ConnectorImportRequest request,
        CancellationToken ct,
        string? actor = null
    )
    {
        var root = catalog.Root(id, "folderIntake");
        var results = new List<ConnectorImportResult>();
        foreach (var path in Directory.EnumerateFiles(root).Take(101))
        {
            if (results.Count == 100)
                break;
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var extension = info.Extension.ToLowerInvariant();
            if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
                continue;
            if (
                info.Length is <= 0 or > 20 * 1024 * 1024
                || (info.Attributes & FileAttributes.ReparsePoint) != 0
            )
            {
                results.Add(new(info.Name, "rejected", null));
                continue;
            }
            var packet = new Packet { Name = info.Name };
            packet.Audit.Add(
                new(DateTimeOffset.UtcNow, "configured folder import", packet.Revision, actor)
            );
            var source = new Source { Name = info.Name, Extension = extension };
            var directory = Path.Combine(store.Root, packet.Id);
            Directory.CreateDirectory(directory);
            try
            {
                // Open source exclusively against writers; bound actual copied bytes, not only metadata.
                using (
                    var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                )
                using (
                    var output = new FileStream(
                        store.SourcePath(packet, source),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                {
                    var buffer = new byte[81920];
                    int count;
                    long total = 0;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += count;
                        if (total > 20 * 1024 * 1024)
                            throw new InvalidDataException();
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                }
                source.PageCount = Pdfs.Validate(store.SourcePath(packet, source), extension);
                source.Sha256 = Pdfs.SourceHash(store.SourcePath(packet, source));
                packet.Sources.Add(source);
                var key =
                    "connector:"
                    + ReferencePolicies.Hash(
                        new
                        {
                            id,
                            source.Name,
                            source.Sha256,
                            request,
                        }
                    );
                var accepted = store.Accept(
                    packet,
                    key,
                    new WorkflowSelection(
                        request.WorkflowId,
                        request.WorkflowVersion,
                        request.TestRun
                    )
                );
                results.Add(
                    new(
                        info.Name,
                        accepted.Packet is null ? "rejected"
                            : accepted.Packet.Id == packet.Id ? "imported"
                            : "duplicate",
                        accepted.Packet?.Id
                    )
                );
                if (accepted.Packet?.Id != packet.Id)
                    Directory.Delete(directory, true);
            }
            catch (OperationCanceledException)
            {
                if (store.Get(packet.Id) is null && Directory.Exists(directory))
                    Directory.Delete(directory, true);
                throw;
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or ArgumentException
                            or PdfSharp.Pdf.IO.PdfReaderException
                )
            {
                if (store.Get(packet.Id) is null && Directory.Exists(directory))
                    Directory.Delete(directory, true);
                results.Add(new(info.Name, "rejected", null));
            }
        }
        return results.ToArray();
    }
}
