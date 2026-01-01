
using DriverLedger.Application.Receipts.Extraction;

namespace DriverLedger.UnitTests.TestHelpers
{
    internal static class ReceiptFixtures
    {
        public static NormalizedReceipt Receipt(
            DateOnly? date = null,
            string? vendor = "Vendor",
            decimal? total = 10m,
            decimal? tax = 1m,
            string currency = "CAD",
            decimal confidence = 0.99m,
            string rawJson = "{}",
            string? normalizedFieldsJson = null)
            => new(
                Date: date,
                Vendor: vendor,
                Total: total,
                Tax: tax,
                Currency: currency,
                Confidence: confidence,
                RawJson: rawJson,
                NormalizedFieldsJson: normalizedFieldsJson
            );
    }
}
