using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace paprika;

public static class Pdfs
{
    public static string SourceHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public static int Validate(string path, string ext)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            throw new InvalidDataException();
        if (ext == ".pdf")
        {
            if (!bytes.AsSpan().StartsWith("%PDF-"u8))
                throw new InvalidDataException();
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            if (pdf.SecuritySettings.IsEncrypted || pdf.PageCount < 1 || pdf.PageCount > 250)
                throw new InvalidDataException();
            return pdf.PageCount;
        }
        bool png =
            ext == ".png"
            && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool jpg =
            ext is ".jpg" or ".jpeg"
            && bytes.Length > 3
            && bytes[0] == 255
            && bytes[1] == 216
            && bytes[2] == 255;
        if (!png && !jpg)
            throw new InvalidDataException();
        using var image = XImage.FromFile(path);
        if (
            image.PixelWidth < 1
            || image.PixelHeight < 1
            || (long)image.PixelWidth * image.PixelHeight > 80_000_000
        )
            throw new InvalidDataException();
        return 1;
    }

    public static byte[] RenderDocument(Packet packet, Document doc, Store store)
    {
        PlatformReleaseGate.Ensure(store, packet);
        return RenderDocumentBytes(packet, doc, store);
    }

    private static byte[] RenderDocumentBytes(Packet packet, Document doc, Store store)
    {
        using var output = new PdfDocument();
        var paths = new Dictionary<string, string>();
        foreach (var id in doc.PageIds)
        {
            var page = packet.Pages.Single(p => p.Id == id);
            var source = packet.Sources.Single(s => s.Id == page.SourceId);
            if (!paths.TryGetValue(source.Id, out var path))
                paths[source.Id] = path = store.SourcePath(packet, source);
            if (source.Extension == ".pdf")
            {
                using var input = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                output.AddPage(input.Pages[page.PageNumber - 1]);
            }
            else
            {
                using var image = XImage.FromFile(path);
                var p = output.AddPage();
                p.Width = XUnit.FromPoint(image.PointWidth);
                p.Height = XUnit.FromPoint(image.PointHeight);
                using var g = XGraphics.FromPdfPage(p);
                g.DrawImage(image, 0, 0, p.Width.Point, p.Height.Point);
            }
        }
        using var buffer = new MemoryStream();
        output.Save(buffer, false);
        return buffer.ToArray();
    }

    public static byte[] Export(Packet packet, Store store)
    {
        (object? Evidence, ApprovedFieldExport? Fields) snapshot = default;
        if (packet.EffectiveWorkflow.Capabilities is not null)
        {
            using var db = store.Open();
            using var tx = db.BeginTransaction(deferred: true);
            snapshot = ReadExportSnapshot(db, tx, packet, store);
        }
        return BuildExport(packet, store, snapshot, doc => RenderDocumentBytes(packet, doc, store));
    }

    internal static byte[] ApprovedDocument(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet,
        Document doc,
        Store store
    ) => ApprovedDocument(db, tx, packet, doc, store, releaseChecked: false);

    private static byte[] ApprovedDocument(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet,
        Document doc,
        Store store,
        bool releaseChecked
    )
    {
        var index = packet.Documents.IndexOf(doc);
        if (index < 0)
            throw new WorkflowException(404, "Document not found.");
        return store.PacketArtifacts.ApprovedBytes(
            db,
            tx,
            packet,
            $"document-{index + 1:000}.pdf",
            () =>
            {
                if (!releaseChecked)
                    PlatformReleaseGate.Ensure(
                        db,
                        tx,
                        packet,
                        new ConnectorCatalog(store.Configuration)
                    );
                return RenderDocumentBytes(packet, doc, store);
            }
        );
    }

    internal static byte[] ApprovedExport(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet,
        Store store
    )
    {
        var snapshot = ReadExportSnapshot(db, tx, packet, store);
        // Reference/extraction/flow evidence can change without a packet revision change.
        // Older snapshots remain immutable, but can never substitute for current evidence.
        string leaf;
        if (snapshot.Fields is { } fields)
        {
            var digest = ExtractionStorage.Hash(new { platform = snapshot.Evidence, fields });
            leaf = $"packet-fields-v{fields.SchemaVersion}-{digest}.zip";
        }
        else
            leaf = snapshot.Evidence is null
                ? "packet.zip"
                : $"packet-{ArtifactStorage.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot.Evidence, Contract.Json))}.zip";
        return store.PacketArtifacts.ApprovedBytes(db, tx, packet, leaf, () =>
            BuildExport(packet, store, snapshot, doc =>
                ApprovedDocument(db, tx, packet, doc, store, releaseChecked: true)
            )
        );
    }

    private static (object? Evidence, ApprovedFieldExport? Fields) ReadExportSnapshot(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet,
        Store store
    )
    {
        PlatformReleaseGate.Ensure(db, tx, packet, new ConnectorCatalog(store.Configuration));
        var evidence = packet.EffectiveWorkflow.Capabilities is null
            ? null
            : PlatformExportEvidence.Read(db, tx, packet);
        return (evidence, ApprovedFieldExport.Read(db, tx, packet));
    }

    private static byte[] BuildExport(
        Packet packet,
        Store store,
        (object? Evidence, ApprovedFieldExport? Fields) snapshot,
        Func<Document, byte[]> renderDocument
    )
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            if (snapshot.Evidence is not null)
            {
                using var evidence = zip.CreateEntry("platform.json").Open();
                JsonSerializer.Serialize(evidence, snapshot.Evidence, Contract.Json);
            }
            if (snapshot.Fields is not null)
            {
                using var fields = zip.CreateEntry("fields.json").Open();
                JsonSerializer.Serialize(fields, snapshot.Fields, Contract.Json);
            }
            int n = 0;
            var mappings = new List<object>();
            foreach (var doc in packet.Documents)
            {
                var name = $"{++n:000}-{doc.Type}.pdf";
                using (var entry = zip.CreateEntry(name).Open())
                {
                    entry.Write(renderDocument(doc));
                }
                mappings.Add(
                    new
                    {
                        file = name,
                        doc.Id,
                        doc.Type,
                        doc.BusinessReference,
                        pages = doc.PageIds.Select(id =>
                        {
                            var p = packet.Pages.Single(x => x.Id == id);
                            return new
                            {
                                pageId = id,
                                p.SourceId,
                                p.SourceName,
                                p.PageNumber,
                            };
                        }),
                    }
                );
            }
            using var manifest = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(
                manifest,
                new
                {
                    packet.Id,
                    packet.Name,
                    packet.Mode,
                    workflow = packet.EffectiveWorkflow,
                    packet.TestRun,
                    packet.Revision,
                    packet.Audit,
                    processing = new
                    {
                        model = packet.Processing?.Model,
                        packet.PromptVersion,
                        packet.TaxonomyVersion,
                    },
                    sources = packet.Sources.Select(s => new
                    {
                        s.Id,
                        s.Name,
                        s.PageCount,
                        sha256 = s.Sha256 ?? SourceHash(store.SourcePath(packet, s)),
                    }),
                    documents = mappings,
                    businessGroups = packet
                        .Documents.GroupBy(d =>
                            string.IsNullOrWhiteSpace(d.BusinessReference)
                                ? null
                                : d.BusinessReference
                        )
                        .Select(g => new
                        {
                            businessReference = g.Key,
                            unassigned = g.Key is null,
                            documentIds = g.Select(d => d.Id).ToArray(),
                        }),
                },
                Contract.Json
            );
        }
        return buffer.ToArray();
    }

    // Minimal standard-font fixture: no installed fonts or external services required.
    public static void Synthetic(string path, IReadOnlyList<string> texts)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<string>();
        foreach (var text in texts)
        {
            int page = objects.Count + 1,
                stream = page + 1;
            kids.Add($"{page} 0 R");
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {stream} 0 R >>"
            );
            string content =
                "BT /F1 14 Tf 50 740 Td "
                + string.Join(
                    " 0 -24 Td ",
                    text.Split('\n')
                        .Select(line =>
                            "("
                            + line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")
                            + ") Tj"
                        )
                )
                + " ET";
            objects.Add(
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
            );
        }
        objects[1] = $"<< /Type /Pages /Count {texts.Count} /Kids [{string.Join(' ', kids)}] >>";
        using var file = File.Create(path);
        void Write(string s)
        {
            file.Write(Encoding.ASCII.GetBytes(s));
        }
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long start = file.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets.Skip(1))
            Write($"{o:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n");
    }
}
