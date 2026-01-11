using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction;

internal static class UberStatementTextFallback
{
    public static List<StatementLineNormalized> TryExtract(string analyzedContent)
    {
        if (string.IsNullOrWhiteSpace(analyzedContent))
            return new List<StatementLineNormalized>();

        if (!Regex.IsMatch(analyzedContent, @"\bUBER\b", RegexOptions.IgnoreCase))
            return new List<StatementLineNormalized>();

        var lines = analyzedContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<StatementLineNormalized>();

        // -----------------------------
        // Section headers
        // -----------------------------
        const string ridesGrossHeader = @"\bUBER\s+RIDES\s*-\s*GROSS\s+FARES\s+BREAKDOWN\b";
        const string ridesFeesHeader = @"\bUBER\s+RIDES\s*-\s*FEES\s+BREAKDOWN\b";
        const string eatsGrossHeader = @"\bUBER\s+EATS\s*-\s*GROSS\s+FARES\s+BREAKDOWN\b";
        const string otherDedHeader = @"\bOTHER\s+POTENTIAL\s+DEDUCTIONS\b";

        // -----------------------------
        // Label patterns inside sections (tables)
        // -----------------------------
        // Matches:
        // - "Gross Uber rides fares"
        // - "Gross Uber rides fares1"
        // - minor suffix variants where the base phrase is preserved
        const string grossUberRidesFaresLabel = @"\bGross\s+Uber\s+rides\s+fares\b";

        // Hard ignore “GST/HST number ... RT0001” type lines
        static bool IsRegistrationNoise(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Example: "Your GST/HST Number 789675428RT0001"
            return Regex.IsMatch(s, @"\bRT\d{4}\b", RegexOptions.IgnoreCase)
                   && Regex.IsMatch(s, @"\bGST\s*/\s*HST\b|\bGST\b|\bHST\b", RegexOptions.IgnoreCase);
        }

        static int FindHeaderIndex(string[] src, string headerPattern)
        {
            for (var i = 0; i < src.Length; i++)
                if (Regex.IsMatch(src[i], headerPattern, RegexOptions.IgnoreCase))
                    return i;

            return -1;
        }

        static (int Start, int End) FindSectionRange(string[] src, string headerPattern, int hardEndExclusive)
        {
            var start = FindHeaderIndex(src, headerPattern);
            if (start < 0) return (-1, -1);

            var end = Math.Min(src.Length, hardEndExclusive);

            // next header after start determines end
            var headers = new[] { ridesGrossHeader, ridesFeesHeader, eatsGrossHeader, otherDedHeader };
            for (var i = start + 1; i < end; i++)
            {
                foreach (var h in headers)
                {
                    if (Regex.IsMatch(src[i], h, RegexOptions.IgnoreCase))
                        return (start, i);
                }
            }

            return (start, end);
        }

        // IMPORTANT: if Uber Eats header exists, we ignore everything after it
        var eatsIdx = FindHeaderIndex(lines, eatsGrossHeader);
        var hardEndExclusive = eatsIdx >= 0 ? eatsIdx : lines.Length;

        var ridesGross = FindSectionRange(lines, ridesGrossHeader, hardEndExclusive);
        var ridesFees = FindSectionRange(lines, ridesFeesHeader, hardEndExclusive);
        var otherDed = FindSectionRange(lines, otherDedHeader, hardEndExclusive);

        var hasRidesGross = ridesGross.Start >= 0;
        var hasRidesFees = ridesFees.Start >= 0;
        var hasOtherDed = otherDed.Start >= 0;

        // Find amount near a label inside a section, skipping registration noise lines
        static decimal? FindAmountNearLabel(
            IReadOnlyList<string> src,
            Func<string, bool> isNoise,
            string labelPattern,
            int start,
            int end,
            int lookAhead = 6)
        {
            end = Math.Min(end, src.Count);

            for (var i = Math.Max(0, start); i < end; i++)
            {
                var line = src[i];
                if (isNoise(line)) continue;

                if (!Regex.IsMatch(line, labelPattern, RegexOptions.IgnoreCase))
                    continue;

                // same line
                var amt = StatementExtractionParsing.ParseAmount(line);
                if (amt.HasValue) return amt.Value;

                // lookahead (layout may be label line followed by amount on next line)
                for (var j = 1; j <= lookAhead && (i + j) < end; j++)
                {
                    var next = src[i + j];
                    if (isNoise(next)) continue;

                    amt = StatementExtractionParsing.ParseAmount(next);
                    if (amt.HasValue) return amt.Value;
                }

                return null;
            }

            return null;
        }

        // For totals: prefer the LAST "Total" that has a real money amount, scoped to the section.
        // This reduces the chance of accidentally selecting an intermediate subtotal from a table.
        static decimal? FindSectionTotal(
            IReadOnlyList<string> src,
            Func<string, bool> isNoise,
            int start,
            int end)
        {
            end = Math.Min(end, src.Count);

            for (var i = Math.Min(end - 1, src.Count - 1); i >= Math.Max(0, start); i--)
            {
                var line = src[i];
                if (isNoise(line)) continue;

                if (!Regex.IsMatch(line, @"\bTotal\b", RegexOptions.IgnoreCase))
                    continue;

                var amt = StatementExtractionParsing.ParseAmount(line);
                if (amt.HasValue) return amt.Value;

                // try next line if layout is "Total" then amount
                if ((i + 1) < end && !isNoise(src[i + 1]))
                {
                    amt = StatementExtractionParsing.ParseAmount(src[i + 1]);
                    if (amt.HasValue) return amt.Value;
                }
            }

            return null;
        }

        // -----------------------------
        // 1) Mileage: ONLY Online Mileage (MetricKey=OnlineKilometers)
        // -----------------------------
        if (hasOtherDed)
        {
            var onlineKm = FindAmountNearLabel(
                lines,
                IsRegistrationNoise,
                @"\bOnline\s+Mileage\b",
                otherDed.Start, otherDed.End);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Metric",
                Description: "Online Mileage (Uber statement)",
                CurrencyCode: "CAD",
                CurrencyEvidence: "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: true,
                MetricKey: "OnlineKilometers",
                MetricValue: onlineKm ?? 0m,
                Unit: "km",
                MoneyAmount: null,
                TaxAmount: null
            ));
        }
        else
        {
            // If statement has rides sections but no Other Deductions, still output OnlineKm=0
            if (hasRidesGross || hasRidesFees)
            {
                results.Add(new StatementLineNormalized(
                    LineDate: DateOnly.MinValue,
                    LineType: "Metric",
                    Description: "Online Mileage (Uber statement)",
                    CurrencyCode: "CAD",
                    CurrencyEvidence: "Inferred",
                    ClassificationEvidence: "Extracted",
                    IsMetric: true,
                    MetricKey: "OnlineKilometers",
                    MetricValue: 0m,
                    Unit: "km",
                    MoneyAmount: null,
                    TaxAmount: null
                ));
            }
        }

        // -----------------------------
        // 2) Uber Rides Gross: preferred revenue anchor + tax collected + gross total
        // -----------------------------
        if (hasRidesGross)
        {
            var taxCollected = FindAmountNearLabel(
                lines,
                IsRegistrationNoise,
                @"\bGST\s*/\s*HST\s+you\s+collected\s+from\s+Riders\b",
                ridesGross.Start, ridesGross.End);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "TaxCollected",
                Description: "GST/HST you collected from Riders",
                CurrencyCode: "CAD",
                CurrencyEvidence: taxCollected.HasValue ? "Extracted" : "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: false,
                MetricKey: null,
                MetricValue: null,
                Unit: null,
                MoneyAmount: 0m,
                TaxAmount: Math.Abs(taxCollected ?? 0m)
            ));

            // Preferred revenue anchor:
            // "Gross Uber rides fares" (some PDFs show "Gross Uber rides fares1")
            var grossUberRidesFares = FindAmountNearLabel(
                lines,
                IsRegistrationNoise,
                grossUberRidesFaresLabel,
                ridesGross.Start, ridesGross.End);

            if (grossUberRidesFares.HasValue)
            {
                results.Add(new StatementLineNormalized(
                    LineDate: DateOnly.MinValue,
                    LineType: "Income",
                    Description: "Gross Uber rides fares",
                    CurrencyCode: "CAD",
                    CurrencyEvidence: "Extracted",
                    ClassificationEvidence: "Extracted",
                    IsMetric: false,
                    MetricKey: null,
                    MetricValue: null,
                    Unit: null,
                    MoneyAmount: Math.Abs(grossUberRidesFares.Value),
                    TaxAmount: null
                ));
            }

            // Keep the section "Total" as a separate line for reconciliation/variance analysis.
            var grossTotal = FindSectionTotal(lines, IsRegistrationNoise, ridesGross.Start, ridesGross.End);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Income",
                Description: "Uber Rides Total (Gross)",
                CurrencyCode: "CAD",
                CurrencyEvidence: grossTotal.HasValue ? "Extracted" : "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: false,
                MetricKey: null,
                MetricValue: null,
                Unit: null,
                MoneyAmount: Math.Abs(grossTotal ?? 0m),
                TaxAmount: null
            ));
        }

        // -----------------------------
        // 3) Uber Rides Fees: ITC + fees total
        // -----------------------------
        if (hasRidesFees)
        {
            var itc = FindAmountNearLabel(
                lines,
                IsRegistrationNoise,
                @"\bGST\s*/\s*HST\s+you\s+paid\s+to\s+Uber\b",
                ridesFees.Start, ridesFees.End);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Itc",
                Description: "GST/HST you paid to Uber",
                CurrencyCode: "CAD",
                CurrencyEvidence: itc.HasValue ? "Extracted" : "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: false,
                MetricKey: null,
                MetricValue: null,
                Unit: null,
                MoneyAmount: 0m,
                TaxAmount: Math.Abs(itc ?? 0m)
            ));

            var feeTotal = FindSectionTotal(lines, IsRegistrationNoise, ridesFees.Start, ridesFees.End);

            results.Add(new StatementLineNormalized(
                LineDate: DateOnly.MinValue,
                LineType: "Fee",
                Description: "Uber Rides Fees Total",
                CurrencyCode: "CAD",
                CurrencyEvidence: feeTotal.HasValue ? "Extracted" : "Inferred",
                ClassificationEvidence: "Extracted",
                IsMetric: false,
                MetricKey: null,
                MetricValue: null,
                Unit: null,
                MoneyAmount: Math.Abs(feeTotal ?? 0m),
                TaxAmount: null
            ));
        }

        // Only return if we found at least rides gross or fees; (other deductions alone is not enough)
        return (hasRidesGross || hasRidesFees) ? results : new List<StatementLineNormalized>();
    }
}
