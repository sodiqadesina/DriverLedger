

using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Receipts;

namespace DriverLedger.UnitTests.Receipts

{

    public sealed class ConfidenceScoringTests
    {
        [Fact]
        public void All_fields_present_returns_full_confidence()
        {
            var receipt = new NormalizedReceipt(
                 Date: new DateOnly(2025, 1, 10),
                 Vendor: "Shell",
                 Total: 50m,
                 Tax: 6.5m,
                 Currency: "CAD",
                 RawJson: "{}"
             );

            var confidence = ReceiptConfidenceCalculator.Compute(receipt);

            confidence.Should().Be(1.0m);
        }

        [Fact]
        public void Missing_date_reduces_confidence()
        {
            var receipt = new NormalizedReceipt(
                Date: null,
                Vendor: "Shell",
                Total: 50m,
                Tax: 6.5m,
                Currency: "CAD",
                RawJson: "{}"
            );


            ReceiptConfidenceCalculator
                .Compute(receipt)
                .Should()
                .Be(0.70m);
        }

        [Fact]
        public void Invalid_total_reduces_confidence_to_point_seven()
        {
            var receipt = new NormalizedReceipt(
                Date: new DateOnly(2025, 1, 10),
                Vendor: "Shell",
                Total: 0m,
                Tax: 2.5m,
                Currency: "CAD",
                RawJson: "{}"
            );

            ReceiptConfidenceCalculator
                .Compute(receipt)
                .Should()
                .Be(0.70m);
        }

    }
}
