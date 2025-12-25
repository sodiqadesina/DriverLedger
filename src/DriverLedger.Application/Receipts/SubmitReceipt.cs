

namespace DriverLedger.Application.Receipts
{
    public sealed record SubmitReceiptResult(Guid ReceiptId, string Status);

    public interface IReceiptService
    {
        Task<SubmitReceiptResult> SubmitAsync(Guid receiptId, CancellationToken ct);
    }
}
