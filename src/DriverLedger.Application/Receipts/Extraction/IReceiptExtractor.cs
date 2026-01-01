

namespace DriverLedger.Application.Receipts.Extraction
{
    public sealed record NormalizedReceipt(
    DateOnly? Date,
    string? Vendor,
    decimal? Total,
    decimal? Tax,
    string Currency,
    decimal Confidence,
    string RawJson,
    string? NormalizedFieldsJson = null
);


    public interface IReceiptExtractor
    {
        Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct);
        string ModelVersion { get; }
    }
}
