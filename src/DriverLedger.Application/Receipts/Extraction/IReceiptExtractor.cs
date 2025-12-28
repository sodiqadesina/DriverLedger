

namespace DriverLedger.Application.Receipts.Extraction
{
    public sealed record NormalizedReceipt(
    DateOnly? Date,
    string? Vendor,
    decimal? Total,
    decimal? Tax,
    string Currency,
    string RawJson
);

    public interface IReceiptExtractor
    {
        Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct);
        string ModelVersion { get; }
    }
}
