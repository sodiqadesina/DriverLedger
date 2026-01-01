

namespace DriverLedger.Application.Receipts.Messages
{
    public sealed record ReceiptHoldV1(
      Guid ReceiptId,
      Guid FileObjectId,
      decimal Confidence,
      string HoldReason,
      string? QuestionsJson
  );

    public sealed record ReceiptReadyV1(
        Guid ReceiptId,
        Guid FileObjectId,
        decimal Confidence
    );
}
