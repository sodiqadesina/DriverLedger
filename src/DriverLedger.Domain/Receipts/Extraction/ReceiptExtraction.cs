
namespace DriverLedger.Domain.Receipts.Extraction
{
    public sealed class ReceiptExtraction : Entity, ITenantScoped
    {
        public required Guid TenantId { get; set; }
        public required Guid ReceiptId { get; set; }

        public required string ModelVersion { get; set; } = "di-receipt-prebuilt-v1";

        // raw DI JSON (string) + normalized fields (string JSON)
        public required string RawJson { get; set; }
        public required string NormalizedFieldsJson { get; set; }

        public required decimal Confidence { get; set; } // 0..1
        public required DateTimeOffset ExtractedAt { get; set; } = DateTimeOffset.UtcNow;

        public Receipt? Receipt { get; set; }
    }
}
