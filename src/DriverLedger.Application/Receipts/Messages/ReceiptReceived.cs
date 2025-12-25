

namespace DriverLedger.Application.Receipts.Messages
{
    // Shared contract for Service Bus payload.
    // Keep this stable once published.
    public sealed record ReceiptReceived(Guid ReceiptId, Guid FileObjectId);
}


