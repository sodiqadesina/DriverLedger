
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Receipts;

namespace DriverLedger.UnitTests.Receipts
{
    public sealed class HoldEvaluationTests
    {
        [Fact]
        public void Low_confidence_triggers_hold()
        {
            var receipt = new NormalizedReceipt(
                Date: null,
                Vendor: "Shell",
                Total: 20m,
                Tax: null,
                Currency: "CAD",
                RawJson: "{}"
            );

            var result = ReceiptHoldEvaluator.Evaluate(receipt, confidence: 0.5m);

            result.IsHold.Should().BeTrue();
            result.Reason.Should().Contain("Low confidence");
        }

        [Fact]
        public void Missing_vendor_triggers_hold()
        {
            var receipt = new NormalizedReceipt(
                Date: new DateOnly(2025, 1, 10),
                Vendor: null,
                Total: 20m,
                Tax: null,
                Currency: "CAD",
                RawJson: "{}"
            );

            ReceiptHoldEvaluator
                .Evaluate(receipt, confidence: 1.0m)
                .IsHold
                .Should()
                .BeTrue();
        }

        [Fact]
        public void Valid_receipt_is_not_hold()
        {
            var receipt = new NormalizedReceipt(
                Date: new DateOnly(2025, 1, 10),
                Vendor: "Shell",
                Total: 20m,
                Tax: 2.6m,
                Currency: "CAD",
                RawJson: "{}"
            );

            ReceiptHoldEvaluator
                .Evaluate(receipt, confidence: 0.9m)
                .IsHold
                .Should()
                .BeFalse();
        }
    }
}
