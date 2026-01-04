using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction;

internal static class LyftStatementTextFallback
{
    public static List<StatementLineNormalized> TryExtract(string analyzedContent)
    {
        if (string.IsNullOrWhiteSpace(analyzedContent))
            return new List<StatementLineNormalized>();

        // Provider check
        if (!Regex.IsMatch(analyzedContent, @"\bLyft\b", RegexOptions.IgnoreCase))
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
        // Prefer the centralized parser so you can keep logic in one place.
        AddRideDistanceMetric();

        // ---- Income (not including GST/HST) ----
        AddIncome("Gross fares", @"\bGross\s+fares\b");
        AddIncome("Bonuses", @"\bBonuses\b");
        AddIncome("Tips", @"\bTips\b(?!.*GST)");

        // ---- Fees ----
        AddFee("Lyft & 3rd party fees", @"\bLyft\s*&\s*3rd\s+party\s+fees\b");
        AddFee("3rd party fees", @"\b3rd\s+party\s+fees\b"); // optional extra

        // ---- Tax collected (you remit) ----
        AddTaxCollected("GST/HST received from passengers", @"\bGST\s*/\s*HST\s+received\s+from\s+passengers\b");
        AddTaxCollected("GST/HST received on bonuses", @"\bGST\s*/\s*HST\s+received\s+on\s+bonuses\b");

        // ---- ITC ----
        AddItc("GST/HST paid on Lyft and 3rd party fees", @"\bGST\s*/\s*HST\s+paid\s+on\s+Lyft\s+and\s+3rd\s+party\s+fees\b");

        // Only return if we found meaningful amounts or metrics
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
                    Description: "Ride distance (Lyft statement)",
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

        void AddIncome(string desc, string labelPattern)
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

        void AddFee(string desc, string labelPattern)
        {
            var amt = FindAmountNearLabel(lines, labelPattern);
            if (!amt.HasValue) return;

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Fee",
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
