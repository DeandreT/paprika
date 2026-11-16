namespace paprika;

public sealed record ConnectorImportRequest(
    string? WorkflowId = null,
    int? WorkflowVersion = null,
    bool TestRun = false
);

public sealed record ConnectorImportResult(string Name, string Status, string? PacketId);

public sealed record ConnectorDeliveryRequest(string PacketId, int Revision);

public sealed record ConnectorReceiptRequest(string BusinessStatus, string ReceiptId);

public sealed record ConnectorDelivery(
    string Id,
    string ConnectorId,
    string PacketId,
    int Revision,
    string IdempotencyKey,
    string ContentHash,
    string Status,
    int Attempts,
    bool TransportAcknowledged,
    string BusinessStatus,
    string? ReceiptId,
    DateTimeOffset CreatedAt,
    string? Error,
    string WorkflowHash = "",
    string? RequestedBy = null,
    string? ReceiptRecordedBy = null,
    string ConnectorBindingHash = "",
    bool SyntheticReferenceEvidence = false
);
