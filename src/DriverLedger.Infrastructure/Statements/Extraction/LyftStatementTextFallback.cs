using System.Globalization;
using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction;

internal static class LyftStatementTextFallback
{
    // Lyft PDFs often lay out like:
    //   <Label>
    //   <Amount>
    // or
    //   <Label>  <Amount>
    //
    // This fallback is INTENTIONALLY “Lyft-specific” and “label-driven” so it doesn’t
    // accidentally pick up random numbers from the page.

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

        // Pre-extract canonical label amounts so we can safely decide duplicates (bonuses vs fees)
        decimal? grossFares = FindMoneyNearLabel(lines, @"\bGross\s+fares\b", lookAhead: 6);
        decimal? bonuses = FindMoneyNearLabel(lines, @"\bBonuses\b", lookAhead: 6);
        decimal? tips = FindMoneyNearLabel(lines, @"\bTips\b(?!.*GST)(?!.*HST)", lookAhead: 6);
        decimal? lyftFees = FindMoneyNearLabel(lines, @"\bLyft\s*&\s*3rd\s+party\s+fees\b", lookAhead: 6);
        decimal? tolls = FindMoneyNearLabel(lines, @"\bTolls\b", lookAhead: 6);

        decimal? taxReceivedFromPassengers = FindMoneyNearLabel(lines, @"\bGST\s*/\s*HST\s+received\s+from\s+passengers\b", lookAhead: 6);
        decimal? taxReceivedOnBonuses = FindMoneyNearLabel(lines, @"\bGST\s*/\s*HST\s+received\s+on\s+bonuses\b", lookAhead: 6);
        decimal? itcPaid = FindMoneyNearLabel(lines, @"\bGST\s*/\s*HST\s+paid\s+on\s+Lyft\s+and\s+3rd\s+party\s+fees\b", lookAhead: 6);

        // ===== Metrics =====
        AddRideDistanceMetric();
        AddTotalRidesMetric(); //  Total Rides is a metric (NOT income) — always metric

        // ===== Income (money) =====
        // Add Gross fares
        if (grossFares.HasValue)
            results.Add(MoneyLine("Income", "Gross fares", grossFares.Value));

        // Add Bonuses — but guard: do not emit a bonuses Income if it exactly equals Lyft fees (likely mis-extract)
        if (bonuses.HasValue)
        {
            if (!lyftFees.HasValue || decimal.Round(bonuses.Value, 2, MidpointRounding.AwayFromZero) != decimal.Round(lyftFees.Value, 2, MidpointRounding.AwayFromZero))
            {
                results.Add(MoneyLine("Income", "Bonuses", bonuses.Value));
            }
        }

        // Add Tips
        if (tips.HasValue)
            results.Add(MoneyLine("Income", "Tips", tips.Value));

        // ===== Fees (money) =====
        if (lyftFees.HasValue)
            results.Add(MoneyLine("Fee", "Lyft & 3rd party fees", lyftFees.Value));

        if (tolls.HasValue)
            results.Add(MoneyLine("Fee", "Tolls", tolls.Value));

        // ===== Tax Collected (tax-only) =====
        // For tax semantics we populate TaxAmount (NOT MoneyAmount).
        if (taxReceivedFromPassengers.HasValue)
            results.Add(TaxLine("TaxCollected", "GST/HST received from passengers", taxReceivedFromPassengers.Value));

        if (taxReceivedOnBonuses.HasValue)
            results.Add(TaxLine("TaxCollected", "GST/HST received on bonuses", taxReceivedOnBonuses.Value));

        // ===== ITC (tax-only) =====
        if (itcPaid.HasValue)
            results.Add(TaxLine("Itc", "GST/HST paid on Lyft and 3rd party fees", itcPaid.Value));

        // Only return if meaningful
        var anyNonZero = results.Any(x =>
            (x.MoneyAmount.HasValue && x.MoneyAmount.Value != 0m) ||
            (x.TaxAmount.HasValue && x.TaxAmount.Value != 0m) ||
            (x.IsMetric && x.MetricValue.HasValue && x.MetricValue.Value != 0m));

        return anyNonZero ? results : new List<StatementLineNormalized>();

        // ---------------- local helpers ----------------

        // create an Income/Fee/Expense line that uses MoneyAmount (TaxAmount stays null)
        StatementLineNormalized MoneyLine(string lineType, string desc, decimal amt) =>
            new(
                LineDate: DateOnly.MinValue,
                LineType: lineType,
                Description: desc,

                IsMetric: false,
                MoneyAmount: Math.Abs(amt),
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: null
            );

        // create a TaxCollected/Itc line that uses TaxAmount (MoneyAmount null)
        StatementLineNormalized TaxLine(string lineType, string desc, decimal amt) =>
            new(
                LineDate: DateOnly.MinValue,
                LineType: lineType,
                Description: desc,

                IsMetric: false,
                MoneyAmount: null,
                MetricValue: null,
                MetricKey: null,
                Unit: null,

                CurrencyCode: "CAD",
                CurrencyEvidence: "Extracted",
                ClassificationEvidence: "Extracted",

                TaxAmount: Math.Abs(amt)
            );

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

        void AddTotalRidesMetric()
        {
            var rides = FindIntNearLabel(lines, @"\bTotal\s+Rides\b", lookAhead: 6);
            if (!rides.HasValue) return;

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Metric",
                Description: "Total Rides",
                CurrencyCode: "CAD",
                CurrencyEvidence: "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: true,
                MetricKey: "Trips",
                MetricValue: rides.Value,
                Unit: "trips",
                MoneyAmount: null,
                TaxAmount: null
            ));
        }
    }

    // ---------------- parsing helpers ----------------

    // Money: we only trust values that look like money (2 decimals),
    // which prevents “104558”, “792383085RP0001”, etc.
    private static readonly Regex MoneyRegex =
        new(@"(?<!\d)(\d{1,3}(?:[,\s]\d{3})*(?:\.\d{2})|\d+\.\d{2})(?!\d)",
            RegexOptions.Compiled);

    private static decimal? FindMoneyNearLabel(IReadOnlyList<string> src, string labelPattern, int lookAhead = 4)
    {
        for (var i = 0; i < src.Count; i++)
        {
            var line = src[i];

            if (!Regex.IsMatch(line, labelPattern, RegexOptions.IgnoreCase))
                continue;

            // 1) same line
            {
                var m = MoneyRegex.Match(line);
                if (m.Success)
                {
                    var amt = StatementExtractionParsing.ParseAmount(m.Value);
                    if (amt.HasValue) return amt.Value;
                }
            }

            // 2) next lines
            for (var j = 1; j <= lookAhead && (i + j) < src.Count; j++)
            {
                var next = src[i + j];

                var m = MoneyRegex.Match(next);
                if (!m.Success) continue;

                var amt = StatementExtractionParsing.ParseAmount(m.Value);
                if (amt.HasValue) return amt.Value;
            }

            return null;
        }

        return null;
    }

    // Total Rides is typically an integer. We treat it as a metric and never as money.
    private static readonly Regex IntRegex =
        new(@"(?<!\d)(\d{1,6})(?!\d)", RegexOptions.Compiled);

    private static decimal? FindIntNearLabel(IReadOnlyList<string> src, string labelPattern, int lookAhead = 4)
    {
        for (var i = 0; i < src.Count; i++)
        {
            var line = src[i];

            if (!Regex.IsMatch(line, labelPattern, RegexOptions.IgnoreCase))
                continue;

            // same line
            {
                var m = IntRegex.Match(line);
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                    return v;
            }

            // next lines
            for (var j = 1; j <= lookAhead && (i + j) < src.Count; j++)
            {
                var next = src[i + j];
                var m = IntRegex.Match(next);
                if (!m.Success) continue;

                if (decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                    return v;
            }

            return null;
        }

        return null;
    }
}
