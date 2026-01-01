

using DriverLedger.Application.Receipts.Extraction;

namespace DriverLedger.Infrastructure.Receipts
{
    public static class ReceiptHoldEvaluator
    {
        public static HoldDecision Evaluate(NormalizedReceipt r, decimal confidence)
        {
            if (confidence < 0.70m)
                return HoldDecision.Hold(
                    "Low confidence extraction",
                    "{\"fields\":[\"date\",\"vendor\",\"total\",\"tax\"]}");

            if (r.Total is null || r.Total <= 0)
                return HoldDecision.Hold(
                    "Invalid total amount",
                    "{\"fields\":[\"total\"]}");

            if (r.Date is null || string.IsNullOrWhiteSpace(r.Vendor))
                return HoldDecision.Hold(
                    "Missing required fields",
                    "{\"fields\":[\"date\",\"vendor\"]}");

            if (r.Tax is not null && r.Total is not null && r.Tax > r.Total)
                return HoldDecision.Hold(
                    "Tax exceeds total",
                    "{\"fields\":[\"tax\"]}");

            return HoldDecision.Pass();
        }
    }

    public sealed record HoldDecision(
        bool IsHold,
        string Reason,
        string QuestionsJson)
    {
        public static HoldDecision Hold(string reason, string questionsJson)
            => new(true, reason, questionsJson);

        public static HoldDecision Pass()
            => new(false, string.Empty, "{}");
    }
}
