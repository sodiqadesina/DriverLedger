using System.Globalization;
using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    internal static class StatementExtractionParsing
    {
        private static readonly Regex CurrencyCodeRegex =
            new("\\b[A-Z]{3}\\b", RegexOptions.Compiled);

        private static readonly Regex NonNumberJunkRegex =
            new(@"[^\d\.\-\(\)]", RegexOptions.Compiled);

        private static readonly Regex MetricNumberRegex =
            new(@"(?<num>-?\d{1,3}(?:[,\s]\d{3})*(?:\.\d+)?|-?\d+(?:\.\d+)?)",
                RegexOptions.Compiled);

        internal static bool TryParseRideDistanceMetric(
            string rowText,
            out string metricKey,
            out decimal metricValue,
            out string unit)
        {
            metricKey = default!;
            metricValue = default;
            unit = default!;

            if (string.IsNullOrWhiteSpace(rowText)) return false;

            var t = rowText.Trim();

            var looksLikeDistance =
                t.Contains("km", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("kilomet", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("mile", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(t, @"\bdistance\b", RegexOptions.IgnoreCase);

            if (!looksLikeDistance) return false;

            var m = MetricNumberRegex.Match(t);
            if (!m.Success) return false;

            var raw = m.Groups["num"].Value;
            raw = raw.Replace(",", "").Replace(" ", "");

            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                return false;

            if (Regex.IsMatch(t, @"\bkm\b|kilomet", RegexOptions.IgnoreCase))
            {
                metricKey = "RideKilometers";
                unit = "km";
                metricValue = n;
                return true;
            }

            if (Regex.IsMatch(t, @"\bmi\b|mile", RegexOptions.IgnoreCase))
            {
                metricKey = "RideKilometers";
                unit = "km";
                metricValue = n * 1.609344m;
                return true;
            }

            return false;
        }

        internal static bool IsMonetaryType(string lineType)
            => lineType is "Income" or "Fee" or "Expense";

        internal static bool IsTaxOnlyType(string lineType)
            => lineType is "TaxCollected" or "Itc";

        internal static DateOnly? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (DateTime.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var dt))
            {
                return DateOnly.FromDateTime(dt);
            }

            return null;
        }

        internal static decimal? ParseAmount(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var cleaned = value.Trim();

            var upper = cleaned.ToUpperInvariant();
            if (upper is "N/A" or "NA" or "NULL" or "-" or "—") return null;

            var negative = cleaned.Contains("(") && cleaned.Contains(")");

            cleaned = cleaned
                .Replace("(", "")
                .Replace(")", "")
                .Replace(",", "")
                .Replace("CAD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("USD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Replace("GBP", "", StringComparison.OrdinalIgnoreCase)
                .Replace("CA$", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$", "")
                .Trim();

            cleaned = cleaned.Replace(" ", "");
            cleaned = NonNumberJunkRegex.Replace(cleaned, "").Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return null;

            if (!decimal.TryParse(
                cleaned,
                NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var amount))
            {
                return null;
            }

            return negative ? -amount : amount;
        }

        internal static string? ExtractCurrency(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var upper = value.ToUpperInvariant();

            // Strong signals first (Uber yearly uses CA$ / C$)
            if (upper.Contains("CA$") || upper.Contains("C$")) return "CAD";
            if (upper.Contains("US$")) return "USD";

            // Explicit ISO codes
            if (upper.Contains("CAD")) return "CAD";
            if (upper.Contains("USD")) return "USD";
            if (upper.Contains("EUR")) return "EUR";
            if (upper.Contains("GBP")) return "GBP";

            // Avoid obvious false positives (Uber yearly header contains "NOT AN OFFICIAL ...")
            // Also block common non-currency 3-letter tokens.
            var match = CurrencyCodeRegex.Match(upper);
            if (match.Success)
            {
                var code = match.Value;

                // Tight allowlist (expand later if needed)
                if (code is "CAD" or "USD" or "EUR" or "GBP")
                    return code;

                // Reject junk tokens that commonly appear in docs
                if (code is "NOT" or "AND" or "THE" or "TAX" or "FOR" or "YOU")
                    return null;
            }

            // "$" fallback (keep but safe)
            if (upper.Contains("$")) return "CAD";

            return null;
        }


        internal static (string CurrencyCode, string Evidence) ResolveCurrencyCode(
            string? lineCurrencyCell,
            string? statementLevelCurrency,
            string? amountCell)
        {
            var c1 = ExtractCurrency(lineCurrencyCell);
            if (!string.IsNullOrWhiteSpace(c1)) return (c1!, "Extracted");

            var c2 = ExtractCurrency(statementLevelCurrency);
            if (!string.IsNullOrWhiteSpace(c2)) return (c2!, "Extracted");

            var c3 = ExtractCurrency(amountCell);
            if (!string.IsNullOrWhiteSpace(c3)) return (c3!, "Inferred");

            return ("CAD", "Inferred");
        }

        internal static bool IsMetricLine(string? rawType, string? description)
        {
            var desc = (description ?? string.Empty).ToLowerInvariant();
            var type = (rawType ?? string.Empty).ToLowerInvariant();

            if (type.Contains("metric")) return true;

            // Total rides must be metric (never income)
            if (desc.Contains("total rides")) return true;

            if (desc.Contains("kilomet") || desc.Contains("kilometer") || desc.Contains("km") ||
                desc.Contains("mile") || desc.Contains("mi"))
                return true;

            if (desc.Contains("trip") && (desc.Contains("count") || desc.Contains("trips")))
                return true;

            if (desc.Contains("online hour") || desc.Contains("online time") || desc.Contains("hours online"))
                return true;

            if (desc.Contains("acceptance rate") || desc.Contains("cancellation rate") ||
                desc.Contains("rating") || desc.Contains("%"))
                return true;

            return false;
        }

        /// <summary>
        /// 
        /// For TaxCollected/Itc rows, Lyft sometimes puts the number in the Amount column.
        /// This normalizes it so TaxAmount is populated.
        /// </summary>
        internal static (decimal? MoneyAmount, decimal? TaxAmount) NormalizeTaxColumns(
            string resolvedLineType,
            decimal? moneyAmount,
            decimal? taxAmount)
        {
            if (resolvedLineType is "TaxCollected" or "Itc")
            {
                // Prefer explicit TaxAmount, else fall back to MoneyAmount
                var v = taxAmount ?? moneyAmount;

                // For tax-only rows we want:
                //   TaxAmount = v
                //   MoneyAmount = 0 (or null)
                return (0m, v);
            }

            // Monetary rows: leave as-is
            return (moneyAmount, taxAmount);
        }

        internal static string ResolveLineType(
            string? rawType,
            decimal? amount,
            decimal? taxAmount,
            string? description)
        {
            var desc = (description ?? string.Empty).ToLowerInvariant();
            var type = (rawType ?? string.Empty).ToLowerInvariant();

            if (IsMetricLine(rawType, description))
                return "Metric";

            if (desc.Contains("itc") ||
                desc.Contains("gst/hst paid") ||
                desc.Contains("tax paid on platform") ||
                desc.Contains("input tax credit"))
                return "Itc";

            if ((desc.Contains("gst") || desc.Contains("hst")) &&
                (desc.Contains("received") || desc.Contains("collected") || desc.Contains("charged") || type.Contains("tax")))
                return "TaxCollected";

            if (desc.Contains("fee") ||
                desc.Contains("commission") ||
                desc.Contains("platform") ||
                desc.Contains("booking") ||
                desc.Contains("airport") ||
                desc.Contains("regulatory") ||
                desc.Contains("toll") ||
                desc.Contains("3rd party") ||
                type.Contains("fee"))
                return "Fee";

            if (desc.Contains("gross") ||
                desc.Contains("fare") ||
                desc.Contains("bonus") ||
                desc.Contains("promotion") ||
                desc.Contains("tip") ||
                type.Contains("income"))
                return "Income";

            if (amount.HasValue)
            {
                if (amount.Value < 0m) return "Fee";
                if (amount.Value > 0m) return "Income";
            }

            if (taxAmount.HasValue && taxAmount.Value != 0m)
                return "TaxCollected";

            return "Other";
        }

        internal static string ResolveClassificationEvidence(string? rawType, string resolvedLineType)
        {
            if (!string.IsNullOrWhiteSpace(rawType) && rawType.Trim().Length >= 3)
                return "Extracted";

            return "Inferred";
        }

        internal static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }

            return null;
        }
    }
}
