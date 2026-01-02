

using System.Globalization;
using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    internal static class StatementExtractionParsing
    {
        private static readonly Regex CurrencyCodeRegex = new("\\b[A-Z]{3}\\b", RegexOptions.Compiled);

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
            var negative = cleaned.Contains("(") && cleaned.Contains(")");

            cleaned = cleaned
                .Replace("(", "", StringComparison.OrdinalIgnoreCase)
                .Replace(")", "", StringComparison.OrdinalIgnoreCase)
                .Replace(",", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$", "", StringComparison.OrdinalIgnoreCase)
                .Replace("CAD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("USD", "", StringComparison.OrdinalIgnoreCase)
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

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
            if (upper.Contains("USD")) return "USD";
            if (upper.Contains("CAD")) return "CAD";
            if (upper.Contains("EUR")) return "EUR";
            if (upper.Contains("GBP")) return "GBP";

            var match = CurrencyCodeRegex.Match(upper);
            if (match.Success) return match.Value;

            if (upper.Contains("$")) return "CAD";

            return null;
        }

        internal static string ResolveLineType(string? typeText, decimal? amount, decimal? taxAmount)
        {
            if (!string.IsNullOrWhiteSpace(typeText))
            {
                var lower = typeText.ToLowerInvariant();
                if (lower.Contains("tax")) return "Tax";
                if (lower.Contains("fee") || lower.Contains("commission")) return "Fee";
                if (lower.Contains("income") || lower.Contains("earning")) return "Income";
            }

            if (taxAmount.HasValue && taxAmount.Value != 0m) return "Tax";
            if (amount.HasValue && amount.Value < 0m) return "Fee";

            return "Income";
        }

        internal static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return null;
        }
    }
}
