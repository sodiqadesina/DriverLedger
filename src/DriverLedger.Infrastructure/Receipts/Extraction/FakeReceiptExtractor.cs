using DriverLedger.Application.Receipts.Extraction;


namespace DriverLedger.Infrastructure.Receipts.Extraction
{
    public sealed class FakeReceiptExtractor : IReceiptExtractor
    {
        public string ModelVersion => "fake-v1";

        public Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct)
        {
            // deterministic test result
            return Task.FromResult(new NormalizedReceipt(
                Date: DateOnly.FromDateTime(DateTime.UtcNow.Date),
                Vendor: "Test Vendor",
                Total: 50.00m,
                Tax: 6.50m,
                Currency: "CAD",
                RawJson: "{\"fake\":true}"
            ));
        }
    }
}
