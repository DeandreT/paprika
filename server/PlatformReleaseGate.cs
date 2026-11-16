using Microsoft.Data.Sqlite;

namespace paprika;

public static class PlatformReleaseGate
{
    public static void Ensure(Store store, Packet packet)
    {
        if (
            packet.EffectiveWorkflow.Capabilities?.Reference?.RequireVerified != true
            && packet.EffectiveWorkflow.Capabilities?.Extraction is null
        )
            return;
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        Ensure(c, tx, packet, new ConnectorCatalog(store.Configuration));
    }

    public static void Ensure(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        ConnectorCatalog? connectors = null
    )
    {
        ExtractionPlatform.EnsureComplete(c, tx, packet);
        if (packet.EffectiveWorkflow.Capabilities?.Reference?.RequireVerified != true)
            return;
        if (!ReferenceService.IsVerified(c, tx, packet, allowSynthetic: true, config: connectors))
            throw new WorkflowException(
                409,
                "Verify required business references for this exact revision before exporting or delivering."
            );
    }
}
