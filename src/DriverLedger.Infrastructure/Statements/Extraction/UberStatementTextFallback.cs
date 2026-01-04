using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction;

internal static class UberStatementTextFallback
{
    // Extracts key Uber rows from analyzed.Content (full text).
    // Returns normalized lines with correct LineType + MoneyAmount/TaxAmount,
    // and emits ride distance as non-posting Metric lines (RideKilometers).
    public static List<StatementLineNormalized> TryExtract(string analyzedContent)
    {
        if (string.IsNullOrWhiteSpace(analyzedContent))
            return new List<StatementLineNormalized>();

        // Quick provider check
        if (!Regex.IsMatch(analyzedContent, @"\bUBER\b", RegexOptions.IgnoreCase))
            return new List<StatementLineNormalized>();

        var lines = analyzedContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<StatementLineNormalized>();

        // Finds the first numeric value on the same line OR within the next N lines after a matching label line.
        static decimal? FindAmountNearLabel(IReadOnlyList<string> src, string labelPattern, int lookAhead = 4)
        {
            for (var i = 0; i < src.Count; i++)
            {
                var l = src[i];

                if (!Regex.IsMatch(l, labelPattern, RegexOptions.IgnoreCase))
                    continue;

                // 1) Try same line
                var amt = StatementExtractionParsing.ParseAmount(l);
                if (amt.HasValue) return amt.Value;

                // 2) Try subsequent lines (common PDF layout: label line then amount line)
                for (var j = 1; j <= lookAhead && (i + j) < src.Count; j++)
                {
                    var next = src[i + j];
                    amt = StatementExtractionParsing.ParseAmount(next);
                    if (amt.HasValue) return amt.Value;
                }

                return null;
            }

            return null;
        }

        // ===== Metrics: Ride distance (source of truth) =====
        AddRideDistanceMetric();

        // ===== Monetary lines (Gross fares breakdown) =====
        AddIncome("Gross Uber rides fares", @"\bGross\s+Uber\s+rides\s+fares\b");
        AddIncome("Booking fee", @"\bBooking\s+fee\b");
        AddIncome("Regulatory Recovery Fees", @"\bRegulatory\s+Recovery\s+Fees\b");
        AddIncome("Airport fee", @"\bAirport\s+fee\b");
        AddIncome("Tips", @"\bTips\b(?!.*GST)", allowMultiple: false);

        // Tax collected from riders (TaxCollected)
        AddTaxCollected("GST/HST you collected from Riders", @"\bGST\s*/\s*HST\s+you\s+collected\s+from\s+Riders\b");

        // Fees (Fees breakdown)
        AddFee("Service Fee", @"\bService\s+Fee\b");
        AddFee("Other amounts", @"\bOther\s+amounts\b");
        AddFee("Fee Discount", @"\bFee\s+Discount\b", allowNegative: true);

        // ITC (tax you paid to Uber)
        AddItc("GST/HST you paid to Uber", @"\bGST\s*/\s*HST\s+you\s+paid\s+to\s+Uber\b");

        // Only return if we found something meaningful
        var anyNonZero = results.Any(x =>
            (x.MoneyAmount.HasValue && x.MoneyAmount.Value != 0m) ||
            (x.TaxAmount.HasValue && x.TaxAmount.Value != 0m) ||
            (x.IsMetric && x.MetricValue.HasValue && x.MetricValue.Value != 0m));

        return anyNonZero ? results : new List<StatementLineNormalized>();

        // ---- local helpers ----

        void AddRideDistanceMetric()
        {
            foreach (var l in lines)
            {
                if (!StatementExtractionParsing.TryParseRideDistanceMetric(l, out var key, out var value, out var unit))
                    continue;

                results.Add(new StatementLineNormalized(
                    LineDate: DateOnly.MinValue,
                    LineType: "Metric",
                    Description: "Ride distance (Uber statement)",
                    CurrencyCode: "CAD",
                    CurrencyEvidence: "Inferred",
                    ClassificationEvidence: "Extracted",
                    IsMetric: true,
                    MetricKey: key,
                    MetricValue: value,
                    Unit: unit,
                    MoneyAmount: null,
                    TaxAmount: null
                ));

                return; // keep first confident match
            }
        }

        void AddIncome(string desc, string labelPattern, bool allowMultiple = false)
        {
            var amt = FindAmountNearLabel(lines, labelPattern);
            if (!amt.HasValue) return;

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Income",
                Description: desc,

                IsMetric: false,
                MoneyAmount: Math.Abs(amt.Value),
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: null
            ));
        }

        void AddFee(string desc, string labelPattern, bool allowNegative = false)
        {
            var amt = FindAmountNearLabel(lines, labelPattern);
            if (!amt.HasValue) return;

            var v = amt.Value;
            if (!allowNegative) v = Math.Abs(v);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Fee",
                Description: desc,

                IsMetric: false,
                MoneyAmount: Math.Abs(v),
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: null
            ));
        }

        void AddTaxCollected(string desc, string labelPattern)
        {
            var amt = FindAmountNearLabel(lines, labelPattern);
            if (!amt.HasValue) return;

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "TaxCollected",
                Description: desc,

                IsMetric: false,
                MoneyAmount: 0m,
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: Math.Abs(amt.Value)
            ));
        }

        void AddItc(string desc, string labelPattern)
        {
            var amt = FindAmountNearLabel(lines, labelPattern);
            if (!amt.HasValue) return;

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Itc",
                Description: desc,

                IsMetric: false,
                MoneyAmount: 0m,
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: Math.Abs(amt.Value)
            ));
        }
    }
}
