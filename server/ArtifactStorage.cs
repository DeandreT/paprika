using System.Security.Cryptography;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Data.Sqlite;

namespace paprika;

// SQLite owns the receipts. Keys are constructed exclusively from checked server IDs,
// ordinal numbers and digests, never display names, arbitrary document IDs or URLs.
internal sealed class ArtifactStorage
{
    private readonly string root;
    private readonly BlobContainerClient? container;
    internal bool Remote => container is not null;

    internal ArtifactStorage(string root, IConfiguration config, SqliteConnection db)
    {
        this.root = Path.GetFullPath(root);
        var section = config.GetSection("paprika:Storage");
        var provider = (section["Provider"] ?? "filesystem").ToLowerInvariant();
        var location = "filesystem";
        if (provider == "azureblob")
        {
            container = BlobContainer(section);
            location = container.Uri.GetLeftPart(UriPartial.Path);
        }
        else if (
            provider != "filesystem"
            || section
                .GetChildren()
                .Any(c => !string.Equals(c.Key, "Provider", StringComparison.OrdinalIgnoreCase))
        )
            throw new InvalidOperationException("Invalid artifact storage configuration.");
        using var tx = db.BeginTransaction();
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS artifact_location(id INTEGER PRIMARY KEY CHECK(id=1),location TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS export_receipts(packet_id TEXT NOT NULL,key TEXT NOT NULL,sha256 TEXT NOT NULL, PRIMARY KEY(packet_id,key));
            INSERT OR IGNORE INTO artifact_location VALUES(1,$location);
            SELECT location FROM artifact_location WHERE id=1;
            """;
        q.Parameters.AddWithValue("$location", location);
        if (!Equals(q.ExecuteScalar(), location))
            throw new InvalidOperationException(
                "Storage location differs from the one recorded for this data directory."
            );
        if (Remote)
        {
            // Also recognizable by offline PowerShell backup tools without a SQLite driver.
            var marker = LocalPath("azure-artifacts.required");
            if (!File.Exists(marker))
                File.WriteAllText(
                    marker,
                    "A coordinated backup of the private Azure Blob container is required.\n"
                );
        }
        tx.Commit();
    }

    private static BlobContainerClient BlobContainer(IConfigurationSection config)
    {
        var name = config["Container"];
        var connection = config["ConnectionString"];
        var endpoint = config["ServiceUri"];
        if (
            name is null
            || !System.Text.RegularExpressions.Regex.IsMatch(
                name,
                @"\A(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\z"
            )
            || (connection is null) == (endpoint is null)
        )
            throw new InvalidOperationException(
                "Specify a private Azure Blob container and exactly one authentication method."
            );
        try
        {
            var options = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03);
            options.Retry.MaxRetries = 2;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            options.Diagnostics.IsLoggingEnabled = false;
            var service = connection is not null
                ? new BlobServiceClient(connection, options)
                : new BlobServiceClient(
                    new Uri(endpoint!),
                    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
                    options
                );
            var uri = service.Uri;
            if (
                uri.UserInfo.Length != 0
                || uri.Query.Length != 0
                || uri.Fragment.Length != 0
                || !(
                    uri.Scheme == "https"
                    || connection is not null
                        && uri.Scheme == "http"
                        && (uri.IsLoopback || uri.Host == "azurite")
                )
            )
                throw new ArgumentException();
            var client = service.GetBlobContainerClient(name);
            // The service omits x-ms-blob-public-access when anonymous access is off.
            if (
                client.GetProperties().Value.PublicAccess is { } access
                && access != PublicAccessType.None
            )
                throw new InvalidOperationException();
            return client;
        }
        catch (Exception)
        {
            // SDK exception details can include endpoints, SAS values or configuration fragments.
            throw new InvalidOperationException(
                "Azure Blob storage requires a reachable private container and valid credentials."
            );
        }
    }

    private static void CheckId(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid artifact identifier.");
    }

    // Reject links beneath the configured private data root, including dangling links.
    private string LocalPath(params string[] segments)
    {
        var path = root;
        foreach (var segment in segments.Prepend(""))
        {
            path = Path.Combine(path, segment);
            if (
                new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null
            )
                throw new IOException("Artifact paths cannot contain filesystem links.");
        }
        return path;
    }

    private static void ValidateSource(Packet p, Source s)
    {
        CheckId(p.Id);
        if (s.Id.Length is 0 or > 64 || !s.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidDataException("Invalid source identifier.");
        if (s.Extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Invalid source type.");
    }

    internal string SourcePath(Packet p, Source s)
    {
        ValidateSource(p, s);
        var path = LocalPath(p.Id, s.Id + s.Extension);
        if (s.ArtifactProvider is null)
            return path; // Staging and legacy/local authoritative originals retain the original layout.
        if (s.ArtifactProvider == "filesystem")
        {
            Verify(Pdfs.SourceHash(path), s.Sha256);
            return path;
        }
        if (s.ArtifactProvider != "azureblob")
            throw Unavailable();
        if (container is null || s.Sha256 is null)
            throw Unavailable();
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (
                    var file = new FileStream(
                        temp,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                    Download(OriginalKey(p, s), file);
                Verify(Pdfs.SourceHash(temp), s.Sha256);
                try
                {
                    File.Move(temp, path);
                }
                catch (IOException) when (File.Exists(path)) { } // Concurrent verified fill.
            }
            finally
            {
                File.Delete(temp);
            }
        }
        Verify(Pdfs.SourceHash(path), s.Sha256);
        return path;
    }

    private static string OriginalKey(Packet p, Source s) =>
        $"packets/{p.Id}/originals/{s.Id}{s.Extension}";

    // The download endpoint always reads from the authoritative provider. Only consumers
    // requiring seekable filesystem paths (PDF/OCR tools) use the working copy.
    internal byte[] OriginalBytes(Packet packet, Source source)
    {
        if (source.ArtifactProvider != "azureblob")
            return File.ReadAllBytes(SourcePath(packet, source));
        ValidateSource(packet, source);
        if (!Remote)
            throw Unavailable();
        using var buffer = new MemoryStream();
        Download(OriginalKey(packet, source), buffer);
        var bytes = buffer.ToArray();
        Verify(Hash(bytes), source.Sha256);
        return bytes;
    }

    internal void PublishOriginals(Packet packet)
    {
        // The default provider writes its authoritative file during validated intake. Azure
        // publishes before committing the packet/envelope; the staged file becomes a cache.
        foreach (var source in packet.Sources)
        {
            var path = SourcePath(packet, source);
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read
            );
            file.Flush(flushToDisk: true);
            Verify(Convert.ToHexStringLower(SHA256.HashData(file)), source.Sha256);
            if (!Remote)
            {
                source.ArtifactProvider = "filesystem";
                continue;
            }
            file.Position = 0;
            var key = OriginalKey(packet, source);
            if (!Upload(key, file))
            {
                using var bytes = new MemoryStream();
                Download(key, bytes);
                Verify(Hash(bytes.ToArray()), source.Sha256);
            }
            source.ArtifactProvider = "azureblob";
        }
    }

    internal byte[] ApprovedBytes(
        SqliteConnection db,
        SqliteTransaction tx,
        Packet packet,
        string leaf,
        Func<byte[]> generate
    )
    {
        CheckId(packet.Id);
        if (packet.Status != "approved" || packet.Revision < 1)
            throw new WorkflowException(409, "Approve this revision before export.");
        var revision = packet.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = $"packets/{packet.Id}/exports/{revision}/{leaf}";
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT sha256 FROM export_receipts WHERE packet_id=$id AND key=$key";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$key", key);
        var existing = q.ExecuteScalar() as string;
        string PhysicalName(string digest) =>
            $"{Path.GetFileNameWithoutExtension(leaf)}-{digest}{Path.GetExtension(leaf)}";
        string PhysicalKey(string digest) =>
            $"packets/{packet.Id}/exports/{revision}/{PhysicalName(digest)}";
        string LocalFile(string digest) =>
            LocalPath(packet.Id, "exports", revision, PhysicalName(digest));
        if (existing is not null)
        {
            if (existing.Length != 64 || !existing.All(char.IsAsciiHexDigit))
                throw Unavailable();
            byte[] saved;
            if (Remote)
            {
                using var bytes = new MemoryStream();
                Download(PhysicalKey(existing), bytes);
                saved = bytes.ToArray();
            }
            else
            {
                try
                {
                    saved = File.ReadAllBytes(LocalFile(existing));
                }
                catch (IOException)
                {
                    throw Unavailable();
                }
            }
            Verify(Hash(saved), existing);
            return saved;
        }
        var output = generate();
        // Write content-addressed physical objects so a crash before the receipt commits
        // cannot make a later retry adopt unrelated or unverified bytes.
        var digest = Hash(output);
        // Record the digest in the physical name as well as in the SQLite receipt.
        if (Remote)
        {
            using var bytes = new MemoryStream(output);
            if (!Upload(PhysicalKey(digest), bytes))
            {
                using var retained = new MemoryStream();
                Download(PhysicalKey(digest), retained);
                Verify(Hash(retained.ToArray()), digest);
            }
        }
        else
        {
            var path = LocalFile(digest);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew))
                {
                    file.Write(output);
                    file.Flush(true);
                }
                if (File.Exists(path))
                    Verify(Pdfs.SourceHash(path), digest);
                else
                    File.Move(temp, path);
            }
            finally
            {
                File.Delete(temp);
            }
        }
        q.CommandText = "INSERT INTO export_receipts VALUES($id,$key,$digest)";
        q.Parameters.AddWithValue("$digest", digest);
        q.ExecuteNonQuery();
        return output;
    }

    private bool Upload(string key, Stream bytes)
    {
        try
        {
            container!.GetBlobClient(key).Upload(bytes, overwrite: false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
            return false;
        }
        catch (Exception)
        {
            throw Unavailable();
        }
    }

    private void Download(string key, Stream destination)
    {
        try
        {
            container!.GetBlobClient(key).DownloadTo(destination);
        }
        catch (Exception)
        {
            throw Unavailable();
        }
    }

    private static WorkflowException Unavailable() =>
        new(503, "Packet artifact storage is unavailable.");

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Verify(string actual, string? expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowException(503, "Packet artifact failed integrity verification.");
    }
}
