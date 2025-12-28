

namespace DriverLedger.Application.Messaging.Events
{
    public sealed record ReceiptExtractedV1(
    Guid ReceiptId,
    Guid FileObjectId,
    decimal Confidence,
    bool IsHold,
    string HoldReason
);
}
