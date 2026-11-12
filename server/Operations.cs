namespace PaperworkClassifier;

public sealed record PacketCounts(
    int Total,
    int Queued,
    int Processing,
    int Review,
    int Approved,
    int Failed,
    int Cancelled
);

public sealed record DeliveryCounts(
    int Queued,
    int Processing,
    int Delivered,
    int Failed,
    int Rejected
);

public sealed record OperationsResponse(
    string Mode,
    PacketCounts Packets,
    int Pages,
    int Documents,
    DeliveryCounts Deliveries,
    int ReviewCorrections
)
{
    public static OperationsResponse Read(Store store, DeliveryStore deliveries)
    {
        var packets = store.All();
        var outbox = deliveries.All();
        int P(string status) => packets.Count(p => p.Status == status);
        int D(string status) => outbox.Count(d => d.Status == status);
        return new(
            "local",
            new(
                packets.Count,
                P("queued"),
                P("processing"),
                P("review"),
                P("approved"),
                P("failed"),
                P("cancelled") + P("canceled")
            ),
            packets.Sum(p => p.Pages.Count),
            packets.Sum(p => p.Documents.Count),
            new(D("queued"), D("processing"), D("delivered"), D("failed"), D("rejected")),
            packets.Sum(p =>
                p.Audit.Count(a =>
                    a.Action is "review saved" or "approval invalidated; review saved"
                )
            )
        );
    }
}
