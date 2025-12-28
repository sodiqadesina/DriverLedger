

using DriverLedger.Application.Receipts.Extraction;

namespace DriverLedger.Infrastructure.Receipts
{
    public static class ReceiptConfidenceCalculator
    {
        public static decimal Compute(NormalizedReceipt r)
        {
            var score = 1.0m;

            if (r.Date is null) score -= 0.30m;
            if (string.IsNullOrWhiteSpace(r.Vendor)) score -= 0.30m;
            if (r.Total is null || r.Total <= 0) score -= 0.30m;
            if (r.Tax is null || r.Tax < 0) score -= 0.10m;

            return Math.Clamp(score, 0, 1);
        }
    }
}
