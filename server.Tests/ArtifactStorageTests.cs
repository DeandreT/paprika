using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class ArtifactStorageTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-storage-" + Guid.NewGuid().ToString("N")
    );

    private Store Open(params (string Key, string Value)[] storage)
    {
        var values = storage.ToDictionary(
            pair => "paprika:Storage:" + pair.Key,
            pair => (string?)pair.Value
        );
        values["paprika:DataDirectory"] = root;
        return new Store(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void InvalidOrAmbiguousStorageConfigurationFailsClosedWithoutEchoingSecrets()
    {
        Assert.Throws<InvalidOperationException>(() => Open(("Provider", "typo")));
        Assert.Throws<InvalidOperationException>(() => Open(("ConnectionString", "secret-marker")));
        Assert.Throws<InvalidOperationException>(() =>
            Open(("Provider", "AzureBlob"), ("Container", "files"))
        );
        var error = Assert.Throws<InvalidOperationException>(() =>
            Open(
                ("Provider", "AzureBlob"),
                ("Container", "files"),
                ("ConnectionString", "secret-marker")
            )
        );
        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Throws<InvalidOperationException>(() =>
            Open(
                ("Provider", "AzureBlob"),
                ("Container", "files"),
                ("ServiceUri", "http://example.invalid")
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            Open(
                ("Provider", "AzureBlob"),
                ("Container", "files"),
                ("ConnectionString", "secret-marker"),
                ("ServiceUri", "https://example.invalid")
            )
        );
    }

    [Fact]
    public void CommittedLocationCannotSilentlyChangeToFilesystemOnRestart()
    {
        var store = Open();
        using var db = new SqliteConnection(store.ConnectionString);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "UPDATE artifact_location SET location='https://fixture.invalid/private'";
        command.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => Open());
    }

    [Fact]
    public void TamperedOriginalCannotBeUsedForNewRunAndInvalidStagingCannotBeAccepted()
    {
        var store = Open();
        var packet = Worker.Demo(store);
        packet.Status = "review";
        store.Save(packet);
        var source = packet.Sources.Single();
        File.AppendAllText(store.SourcePath(packet, source), "changed");
        var error = Assert.Throws<WorkflowException>(() =>
            store.Reprocess(
                packet.Id,
                packet.Revision,
                new Settings(new ConfigurationBuilder().Build())
            )
        );
        Assert.Equal(503, error.Code);
        Assert.Single(store.All());

        var previous = new Packet();
        previous.Sources.Add(new() { Id = "../unsafe", Extension = ".pdf" });
        Assert.Throws<InvalidDataException>(() => store.Accept(previous, null));
        Assert.Null(store.Get(previous.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
