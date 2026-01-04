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

        // support thousands separators like 5,178.846 (and OCR variants with spaces)
        // Matches:
        // - 5178
        // - 5178.846
        // - 5,178.846
        // - 5 178.846
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

            // Strong signals
            var looksLikeDistance =
                t.Contains("km", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("kilomet", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("mile", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(t, @"\bdistance\b", RegexOptions.IgnoreCase);

            if (!looksLikeDistance) return false;

            //  Extract numeric including thousands separators
            var m = MetricNumberRegex.Match(t);
            if (!m.Success) return false;

            var raw = m.Groups["num"].Value;

            //  Normalize separators: remove commas and spaces used as thousand separators
            // Keep decimal dot intact.
            raw = raw.Replace(",", "").Replace(" ", "");

            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                return false;

            // Determine unit
            if (Regex.IsMatch(t, @"\bkm\b|kilomet", RegexOptions.IgnoreCase))
            {
                metricKey = "RideKilometers";
                unit = "km";
                metricValue = n;
                return true;
            }

            if (Regex.IsMatch(t, @"\bmi\b|mile", RegexOptions.IgnoreCase))
            {
                // store canonical km as source of truth (convert miles -> km)
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

        /// <summary>
        /// Parse a currency/amount string into a decimal.
        /// IMPORTANT: return null if it cannot be parsed (do NOT return 0 as a fallback).
        /// </summary>
        internal static decimal? ParseAmount(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var cleaned = value.Trim();

            // Common "not a number" tokens coming from OCR/DI tables
            var upper = cleaned.ToUpperInvariant();
            if (upper is "N/A" or "NA" or "NULL" or "-" or "—") return null;

            // Parentheses mean negative in many statements: (123.45)
            var negative = cleaned.Contains("(") && cleaned.Contains(")");

            // Remove obvious currency markers / separators
            cleaned = cleaned
                .Replace("(", "")
                .Replace(")", "")
                .Replace(",", "")
                .Replace("CAD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("USD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Replace("GBP", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$", "")
                .Trim();

            // Remove any remaining junk except digits, dot, minus
            cleaned = NonNumberJunkRegex.Replace(cleaned, "").Trim();

            // If after cleaning we have nothing meaningful, return null
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
            if (upper.Contains("CAD")) return "CAD";
            if (upper.Contains("USD")) return "USD";
            if (upper.Contains("EUR")) return "EUR";
            if (upper.Contains("GBP")) return "GBP";

            var match = CurrencyCodeRegex.Match(upper);
            if (match.Success) return match.Value;

            // Default $ to CAD (your product is Canada-first)
            if (upper.Contains("$")) return "CAD";

            return null;
        }

        /// <summary>
        /// Prefer explicit extracted currency (line cell, statement header).
        /// If nothing is found, infer CAD as product default.
        /// Returns (CurrencyCode, Evidence).
        /// </summary>
        internal static (string CurrencyCode, string Evidence) ResolveCurrencyCode(
            string? lineCurrencyCell,
            string? statementLevelCurrency,
            string? amountCell)
        {
            // strongest: explicit currency cell
            var c1 = ExtractCurrency(lineCurrencyCell);
            if (!string.IsNullOrWhiteSpace(c1)) return (c1!, "Extracted");

            // next: statement-level currency extracted elsewhere
            var c2 = ExtractCurrency(statementLevelCurrency);
            if (!string.IsNullOrWhiteSpace(c2)) return (c2!, "Extracted");

            // next: infer from amount cell symbols (e.g., "$")
            var c3 = ExtractCurrency(amountCell);
            if (!string.IsNullOrWhiteSpace(c3)) return (c3!, "Inferred");

            // final fallback: Canada-first default
            return ("CAD", "Inferred");
        }

        internal static bool IsMetricLine(string? rawType, string? description)
        {
            var desc = (description ?? string.Empty).ToLowerInvariant();
            var type = (rawType ?? string.Empty).ToLowerInvariant();

            if (type.Contains("metric")) return true;

            // distance / mileage
            if (desc.Contains("kilomet") || desc.Contains("kilometer") || desc.Contains("km") ||
                desc.Contains("mile") || desc.Contains("mi"))
                return true;

            // trip counts / activity
            if (desc.Contains("trip") && (desc.Contains("count") || desc.Contains("trips")))
                return true;

            if (desc.Contains("online hour") || desc.Contains("online time") || desc.Contains("hours online"))
                return true;

            // rate-style metrics
            if (desc.Contains("acceptance rate") || desc.Contains("cancellation rate") ||
                desc.Contains("rating") || desc.Contains("%"))
                return true;

            return false;
        }

        internal static (string? MetricKey, string? Unit) ResolveMetricKeyAndUnit(
            string? rawType,
            string? description)
        {
            var desc = (description ?? string.Empty).ToLowerInvariant();
            var type = (rawType ?? string.Empty).ToLowerInvariant();

            if (desc.Contains("kilomet") || desc.Contains("kilometer") || desc.Contains(" km") || desc == "km")
                return ("RideKilometers", "km");

            if (desc.Contains(" mile") || desc.Contains(" miles") || desc.Contains(" mi") || desc == "mi")
                return ("RideMiles", "mi");

            if ((desc.Contains("trip") && desc.Contains("count")) || desc.Contains("total trips") || desc == "trips")
                return ("Trips", "trips");

            if (desc.Contains("online hour") || desc.Contains("online time") || desc.Contains("hours online"))
                return ("OnlineHours", "hours");

            if (type.Contains("metric"))
                return ("Metric", null);

            return (null, null);
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
                (desc.Contains("collected") || desc.Contains("charged") || type.Contains("tax")))
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
                desc.Contains("trip") ||
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
