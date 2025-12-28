using DriverLedger.Application.Receipts.Extraction;
using System.Text.Json;


namespace DriverLedger.Infrastructure.Receipts.Extraction
{
    public sealed class FakeReceiptExtractor : IReceiptExtractor
    {
        public string ModelVersion => "fake-v1";

        public Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct)
        {
            // Keep fake output deterministic and useful for tests/dev
            var raw = JsonSerializer.Serialize(new
            {
                fake = true,
                ts = DateTimeOffset.UtcNow
            });

            var normalizedFields = JsonSerializer.Serialize(new
            {
                modelId = "fake-receipt",
                merchantName = "Fake Vendor",
                currency = "CAD",
                items = Array.Empty<object>()
            });

            return Task.FromResult(new NormalizedReceipt(
                Date: DateOnly.FromDateTime(DateTime.UtcNow),
                Vendor: "Fake Vendor",
                Total: 12.34m,
                Tax: 1.23m,
                Currency: "CAD",
                Confidence: 0.99m,
                RawJson: raw,
                NormalizedFieldsJson: normalizedFields
            ));
        }
    }
}
