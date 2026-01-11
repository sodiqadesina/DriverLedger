using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using DriverLedger.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public sealed class AzureDocumentIntelligenceStatementMetadataExtractor : IStatementMetadataExtractor
    {
        private static readonly string[] PeriodKeyHints =
        [
            "statement period",
            "period",
            "date range",
            "for period",
            "quarterly period"
        ];

        private static readonly string[] PeriodStartHints =
        [
            "period start",
            "start date",
            "from"
        ];

        private static readonly string[] PeriodEndHints =
        [
            "period end",
            "end date",
            "to"
        ];

        private static readonly string[] TotalAmountHints =
        [
            "total",
            "amount due",
            "statement total",
            "gross total",
            "net earnings",
            "total earnings"
        ];

        private static readonly string[] TaxAmountHints =
        [
            "tax",
            "gst",
            "hst",
            "vat"
        ];

        private static readonly Regex DateTokenRegex = new(
            "\\b(?:\\d{1,2}[/-]\\d{1,2}[/-]\\d{2,4}|\\d{4}[/-]\\d{1,2}[/-]\\d{1,2}|[A-Za-z]{3,9}\\s+\\d{1,2},?\\s+\\d{4}|[A-Za-z]{3,9}\\s+\\d{4})\\b",
            RegexOptions.Compiled);

        private readonly DocumentAnalysisClient _client;
        private readonly ILogger<AzureDocumentIntelligenceStatementMetadataExtractor> _logger;
        private readonly string _modelId;

        public string ModelVersion => _modelId;

        public AzureDocumentIntelligenceStatementMetadataExtractor(
            IOptions<DocumentIntelligenceOptions> options,
            ILogger<AzureDocumentIntelligenceStatementMetadataExtractor> logger)
        {
            _logger = logger;

            var o = options.Value;

            if (string.IsNullOrWhiteSpace(o.Endpoint))
                throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint is missing.");
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                throw new InvalidOperationException("Azure:DocumentIntelligence:ApiKey is missing.");

            _modelId = string.IsNullOrWhiteSpace(o.StatementsMetadataModelId)
                ? "prebuilt-layout"
                : o.StatementsMetadataModelId.Trim();

            _client = new DocumentAnalysisClient(new Uri(o.Endpoint), new AzureKeyCredential(o.ApiKey));
        }

        public bool CanHandleContentType(string contentType)
            => contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

        public async Task<StatementMetadataResult> ExtractAsync(Stream file, CancellationToken ct)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (file.CanSeek) file.Position = 0;

            _logger.LogInformation("Analyzing statement metadata via Azure Document Intelligence ({ModelId})", _modelId);

            AnalyzeDocumentOperation op = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                file,
                cancellationToken: ct);

            AnalyzeResult result = op.Value;

            var provider = ExtractProvider(result);
            var (periodStart, periodEnd) = ExtractPeriodRange(result);

            // If still missing, fallback to a whole-document scan for date tokens
            if (!periodStart.HasValue || !periodEnd.HasValue)
            {
                var allText = string.Join("\n",
                    result.Pages
                        .OrderBy(p => p.PageNumber)
                        .SelectMany(p => p.Lines)
                        .Select(l => l.Content)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                if (TryExtractDateRange(allText, out var s, out var e) && s.HasValue)
                    periodStart ??= s;
                if (e.HasValue)
                    periodEnd ??= e;

                // If we only found a year, treat as yearly
                if (periodStart.HasValue && !periodEnd.HasValue)
                {
                    periodEnd = new DateOnly(periodStart.Value.Year, 12, 31);
                }
            }

            var periodType = DerivePeriodType(periodStart, periodEnd) ?? "Unknown";
            var periodKey = DerivePeriodKey(periodStart, periodEnd, periodType) ?? "Unknown";

            var vendorName = ExtractVendorName(result);
            var (statementTotalAmount, taxAmount, currency) = ExtractTotals(result);

            return new StatementMetadataResult
            {
                Provider = provider ?? vendorName,
                PeriodType = periodType,
                PeriodKey = periodKey,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                VendorName = vendorName,
                StatementTotalAmount = statementTotalAmount,
                TaxAmount = taxAmount,
                Currency = currency
            };
        }

        private static string? ExtractProvider(AnalyzeResult result)
        {
            var lines = result.Pages
                .OrderBy(p => p.PageNumber)
                .SelectMany(p => p.Lines)
                .Select(l => l.Content?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            var top = string.Join("\n", lines.Take(80));

            string? FindBrand(string s)
            {
                var brands = new[] { "Uber", "Lyft", "DoorDash", "SkipTheDishes", "Instacart" };
                foreach (var b in brands)
                    if (s.Contains(b, StringComparison.OrdinalIgnoreCase)) return b;
                return null;
            }

            var brand = FindBrand(top);
            if (!string.IsNullOrWhiteSpace(brand))
                return brand;

            foreach (var l in lines.Take(40))
            {
                if (string.IsNullOrWhiteSpace(l)) continue; // FIX CS8602
                if (l.Length < 3 || l.Length > 80) continue;
                if (l.Contains("not an official", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("tax document", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("invoice", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("statement", StringComparison.OrdinalIgnoreCase)) continue;
                if (l.Contains("summary", StringComparison.OrdinalIgnoreCase)) continue;
                return l;
            }

            return null;
        }


        private static bool TryParseQuarterlyPeriod(string text, out DateOnly start, out DateOnly end, out string periodKey)
        {
            // Example:
            // "Quarterly Period January 1, 2024 - March 31, 2024"
            // Support hyphen variants and extra whitespace.
            var m = Regex.Match(
                text,
                @"Quarterly\s+Period\s+([A-Za-z]+\s+\d{1,2},\s+\d{4})\s*[-–]\s*([A-Za-z]+\s+\d{1,2},\s+\d{4})",
                RegexOptions.IgnoreCase);

            if (!m.Success)
            {
                start = default;
                end = default;
                periodKey = "";
                return false;
            }

            if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var sdt) ||
                !DateTime.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var edt))
            {
                start = default;
                end = default;
                periodKey = "";
                return false;
            }

            start = DateOnly.FromDateTime(sdt);
            end = DateOnly.FromDateTime(edt);

            var q = ((start.Month - 1) / 3) + 1;
            periodKey = $"{start.Year:D4}-Q{q}";
            return true;
        }

        private static bool LooksLikeLyft(string text)
        {
            return Regex.IsMatch(text, @"\bLyft\b", RegexOptions.IgnoreCase);
        }

        private static string? ExtractVendorName(AnalyzeResult result)
        {
            var keyValueVendor = result.KeyValuePairs?.FirstOrDefault(kvp =>
                kvp.Key?.Content?.Contains("vendor", StringComparison.OrdinalIgnoreCase) == true ||
                kvp.Key?.Content?.Contains("merchant", StringComparison.OrdinalIgnoreCase) == true);

            if (keyValueVendor?.Value?.Content is { Length: > 0 } vendorValue)
                return vendorValue.Trim();

            return null;
        }

        private static (DateOnly? Start, DateOnly? End) ExtractPeriodRange(AnalyzeResult result)
        {
            var keyValuePairs = result.KeyValuePairs ?? Enumerable.Empty<DocumentKeyValuePair>();

            DateOnly? start = null;
            DateOnly? end = null;

            foreach (var kvp in keyValuePairs)
            {
                var key = kvp.Key?.Content;
                var value = kvp.Value?.Content;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (KeyMatches(key, PeriodKeyHints))
                {
                    if (TryExtractDateRange(value, out start, out end))
                        return (start, end);
                }

                if (KeyMatches(key, PeriodStartHints) && start is null)
                {
                    if (TryExtractSingleDate(value, out var parsedStart))
                        start = parsedStart;
                }

                if (KeyMatches(key, PeriodEndHints) && end is null)
                {
                    if (TryExtractSingleDate(value, out var parsedEnd))
                        end = parsedEnd;
                }
            }

            if (start.HasValue && end.HasValue)
                return (start, end);

            // Scan more header lines and look for "Quarterly Period" and similar patterns
            var headerLines = result.Pages
                .OrderBy(p => p.PageNumber)
                .SelectMany(p => p.Lines)
                .Select(line => line.Content)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(120)
                .ToList();

            foreach (var line in headerLines)
            {
                if (line is null) continue;

                if (line.Contains("period", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("date range", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("statement period", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("quarterly period", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryExtractDateRange(line, out start, out end))
                        return (start, end);
                }
            }

            // Last fallback: take first 2 date tokens in the doc (usually the range)
            var allText = string.Join("\n", headerLines);
            var dates = ExtractDateTokens(allText)
                .Select(t => StatementExtractionParsing.ParseDate(t))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            if (dates.Count >= 2)
                return (dates[0], dates[1]);

            return (start, end);
        }

        private static (decimal? StatementTotalAmount, decimal? TaxAmount, string? Currency) ExtractTotals(AnalyzeResult result)
        {
            decimal? totalAmount = null;
            decimal? taxAmount = null;
            string? currency = null;

            foreach (var kvp in result.KeyValuePairs ?? [])
            {
                var key = kvp.Key?.Content;
                var value = kvp.Value?.Content;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (totalAmount is null && KeyMatches(key, TotalAmountHints))
                {
                    totalAmount = StatementExtractionParsing.ParseAmount(value);
                    currency ??= StatementExtractionParsing.ExtractCurrency(value);
                }

                if (taxAmount is null && KeyMatches(key, TaxAmountHints))
                {
                    taxAmount = StatementExtractionParsing.ParseAmount(value);
                    currency ??= StatementExtractionParsing.ExtractCurrency(value);
                }
            }

            if (currency is null)
            {
                var headerLines = result.Pages
                    .OrderBy(p => p.PageNumber)
                    .SelectMany(p => p.Lines)
                    .Select(line => line.Content)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Take(50)
                    .ToList();

                currency = headerLines
                    .Select(line => StatementExtractionParsing.ExtractCurrency(line))
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
            }

            // Fallback: scan page text lines (needed for Uber yearly summaries)
            if (totalAmount is null || taxAmount is null || currency is null)
            {
                var lines = result.Pages
                    .OrderBy(p => p.PageNumber)
                    .SelectMany(p => p.Lines)
                    .Select(l => l.Content?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(300)
                    .ToList();

                if (totalAmount is null)
                {
                    foreach (var line in lines)
                    {
                        if (line is null) continue;
                        if (TotalAmountHints.Any(h => line.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        {
                            var amt = StatementExtractionParsing.ParseAmount(line);
                            if (amt.HasValue)
                            {
                                totalAmount = amt;
                                currency ??= StatementExtractionParsing.ExtractCurrency(line);
                                break;
                            }
                        }
                    }
                }

                if (taxAmount is null)
                {
                    foreach (var line in lines)
                    {
                        if (line is null) continue;
                        if (TaxAmountHints.Any(h => line.Contains(h, StringComparison.OrdinalIgnoreCase)))
                        {
                            var amt = StatementExtractionParsing.ParseAmount(line);
                            if (amt.HasValue)
                            {
                                taxAmount = amt;
                                currency ??= StatementExtractionParsing.ExtractCurrency(line);
                                break;
                            }
                        }
                    }
                }

                currency ??= lines
                    .Select(StatementExtractionParsing.ExtractCurrency)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            }


            return (totalAmount, taxAmount, currency);
        }

        private static bool KeyMatches(string? key, string[] hints)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            return hints.Any(hint => key.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryExtractSingleDate(string text, out DateOnly value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var tokens = ExtractDateTokens(text);
            foreach (var token in tokens)
            {
                var parsed = StatementExtractionParsing.ParseDate(token);
                if (parsed.HasValue)
                {
                    value = parsed.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractDateRange(string text, out DateOnly? start, out DateOnly? end)
        {
            start = null;
            end = null;

            if (string.IsNullOrWhiteSpace(text)) return false;

            //  Uber fix: detects embedded "2024/11" inside a larger sentence.
            if (TryParseMonthYear(text, out var monthStart, out var monthEnd))
            {
                start = monthStart;
                end = monthEnd;
                return true;
            }

            var dates = ExtractDateTokens(text)
                .Select(token => StatementExtractionParsing.ParseDate(token))
                .Where(parsed => parsed.HasValue)
                .Select(parsed => parsed!.Value)
                .ToList();

            if (dates.Count >= 2)
            {
                start = dates[0];
                end = dates[1];
                return true;
            }

            if (dates.Count == 1)
            {
                start = dates[0];
                return true;
            }

            return false;
        }

        private static IEnumerable<string> ExtractDateTokens(string text)
        {
            return DateTokenRegex.Matches(text)
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // ✅ CHANGED: previously required exact string match (e.g., "yyyy/MM" ONLY).
        // Now finds month/year embedded in text like: "Tax summary for the period 2024/11".
        private static bool TryParseMonthYear(string text, out DateOnly start, out DateOnly end)
        {
            start = default;
            end = default;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            // 1) First: search for embedded YYYY/MM or YYYY-MM in any sentence
            var m = Regex.Match(text, @"\b(20\d{2})\s*[/\-]\s*(0?[1-9]|1[0-2])\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                var firstDay = new DateOnly(year, month, 1);
                var lastDay = firstDay.AddMonths(1).AddDays(-1);

                start = firstDay;
                end = lastDay;
                return true;
            }

            // 2) Then: allow "MMMM yyyy" / "MMM yyyy" inside text (less common for Uber, but safe)
            // Example: "Statement period: November 2024"
            var monthName = Regex.Match(text, @"\b([A-Za-z]{3,9})\s+(20\d{2})\b");
            if (monthName.Success)
            {
                var candidate = $"{monthName.Groups[1].Value} {monthName.Groups[2].Value}";
                var formats = new[] { "MMMM yyyy", "MMM yyyy" };

                foreach (var format in formats)
                {
                    if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        var firstDay = new DateTime(dt.Year, dt.Month, 1);
                        var lastDay = firstDay.AddMonths(1).AddDays(-1);
                        start = DateOnly.FromDateTime(firstDay);
                        end = DateOnly.FromDateTime(lastDay);
                        return true;
                    }
                }
            }

            // 3) Last: year-only fallback
            var yearMatch = Regex.Match(text, "\\b(20\\d{2})\\b");
            if (yearMatch.Success)
            {
                var year = int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                start = new DateOnly(year, 1, 1);
                end = new DateOnly(year, 12, 31);
                return true;
            }

            return false;
        }

        private static string? DerivePeriodType(DateOnly? start, DateOnly? end)
        {
            if (!start.HasValue || !end.HasValue) return null;

            // Monthly: same month
            if (start.Value.Year == end.Value.Year && start.Value.Month == end.Value.Month)
                return "Monthly";

            // Quarterly: same year, start is first day of quarter, end is last day of quarter
            if (start.Value.Year == end.Value.Year)
            {
                var qStartMonth = ((start.Value.Month - 1) / 3) * 3 + 1; // 1,4,7,10
                var qEndMonth = qStartMonth + 2;

                var startIsQuarterStart = start.Value.Month == qStartMonth && start.Value.Day == 1;
                var endIsQuarterEnd =
                    end.Value.Month == qEndMonth &&
                    end.Value.Day == DateTime.DaysInMonth(end.Value.Year, end.Value.Month);

                if (startIsQuarterStart && endIsQuarterEnd)
                    return "Quarterly";

                return "Yearly";
            }

            return "Yearly";
        }

        private static string? DerivePeriodKey(DateOnly? start, DateOnly? end, string? periodType)
        {
            if (!start.HasValue || !end.HasValue || string.IsNullOrWhiteSpace(periodType)) return null;

            if (periodType == "Monthly")
                return start.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture);

            if (periodType == "Quarterly")
            {
                var q = ((start.Value.Month - 1) / 3) + 1;
                return $"{start.Value.Year:D4}-Q{q}";
            }

            // Yearly
            return start.Value.Year.ToString(CultureInfo.InvariantCulture);
        }
    }
}
