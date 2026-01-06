using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Receipts.Extraction
{
    public sealed class AzureDocumentIntelligenceReceiptExtractor : IReceiptExtractor
    {
        private readonly DocumentAnalysisClient _client;
        private readonly ILogger<AzureDocumentIntelligenceReceiptExtractor> _logger;
        private readonly string _modelId;

        public string ModelVersion => _modelId;

        public AzureDocumentIntelligenceReceiptExtractor(
            IOptions<DocumentIntelligenceOptions> options,
            ILogger<AzureDocumentIntelligenceReceiptExtractor> logger)
        {
            _logger = logger;

            var o = options.Value;

            if (string.IsNullOrWhiteSpace(o.Endpoint))
                throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint is missing.");
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                throw new InvalidOperationException("Azure:DocumentIntelligence:ApiKey is missing.");

            _modelId = string.IsNullOrWhiteSpace(o.ReceiptsModelId) ? "prebuilt-receipt" : o.ReceiptsModelId.Trim();

            _client = new DocumentAnalysisClient(new Uri(o.Endpoint), new AzureKeyCredential(o.ApiKey));
        }

        public async Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (file.CanSeek) file.Position = 0;

            _logger.LogInformation("Analyzing receipt via Azure Document Intelligence ({ModelId})", _modelId);

            AnalyzeDocumentOperation op = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                file,
                cancellationToken: ct);

            AnalyzeResult result = op.Value;
            var doc = result.Documents.FirstOrDefault();

            // Stable “raw” projection (safer than serializing AnalyzeResult directly)
            var rawJson = BuildRawProjectionJson(result, doc);

            // Core fields (with fallbacks)
            var vendor =
                GetFirstString(doc, "MerchantName", "Merchant", "VendorName")
                ?? GetFirstString(doc, "MerchantAddress", "MerchantPhoneNumber"); // last-resort fallback

            // ----------------------------
            // Date
            // ----------------------------
            DateOnly? date = null;

            var dt = GetFirstDate(doc, "TransactionDate", "Date");
            if (dt is not null)
                date = DateOnly.FromDateTime(dt.Value);

            // If DI returned a suspicious year (ex: 2004), try parsing the original string
            if (date is null || date.Value.Year < 2015)
            {
                var dateText = GetFirstString(doc, "TransactionDate", "Date");
                var reparsed = ParseReceiptDate(dateText);
                if (reparsed is not null) date = reparsed;
            }

            // ----------------------------
            // Amounts
            // ----------------------------
            var total = GetFirstMoneyAmount(doc, "Total", "TotalAmount", "Amount");
            var subtotal = GetFirstMoneyAmount(doc, "Subtotal");
            var tax = GetFirstMoneyAmount(doc, "TotalTax", "TotalVAT", "Tax", "TaxAmount");

            // Fallback 1: derive tax = total - subtotal (only if subtotal seems sane)
            if (tax is null || tax <= 0)
            {
                if (total is not null && subtotal is not null && total.Value >= subtotal.Value)
                {
                    var derived = total.Value - subtotal.Value;
                    if (derived > 0 && derived <= total.Value)
                        tax = Math.Round(derived, 2, MidpointRounding.AwayFromZero);
                }
            }

            // Fallback 2 (FIXED): sum GST/HST/PST/QST from OCR text (supports "label" then amount on next line)
            if (tax is null || tax <= 0)
            {
                var summed = TrySumTaxFromText(result.Content, total);
                if (summed is not null && summed > 0)
                    tax = summed;
            }

            // Currency
            var currency =
                GetFirstCurrency(doc, "Total", "TotalAmount", "TotalTax", "Tax")
                ?? "CAD";

            // DI confidence only (policy confidence stays in ReceiptConfidenceCalculator)
            var diConfidence = ComputeDiConfidence(doc);

            var normalizedFieldsJson = BuildNormalizedFieldsJson(doc, currency);

            _logger.LogInformation("Receipt normalized: vendor={Vendor}, date={Date}, total={Total}, subtotal={Subtotal}, tax={Tax}, currency={Currency}",
                vendor, date?.ToString("yyyy-MM-dd"), total, subtotal, tax, currency);

            return new NormalizedReceipt(
                Date: date,
                Vendor: vendor,
                Total: total,
                Tax: tax,
                Currency: currency,
                Confidence: diConfidence,
                RawJson: rawJson,
                NormalizedFieldsJson: normalizedFieldsJson
            );
        }

        // ---------------- Raw Projection ----------------

        private string BuildRawProjectionJson(AnalyzeResult result, AnalyzedDocument? doc)
        {
            var projection = new
            {
                modelId = _modelId,
                createdOn = DateTime.UtcNow,
                docCount = result.Documents.Count,
                pageCount = result.Pages.Count,

                // include OCR text to enable robust fallback parsing (GST/PST lines etc.)
                content = result.Content,

                fields = doc?.Fields.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        content = kvp.Value?.Content,
                        confidence = kvp.Value?.Confidence
                    })
            };

            return JsonSerializer.Serialize(projection);
        }

        // ---------------- Confidence ----------------

        private static decimal ComputeDiConfidence(AnalyzedDocument? doc)
        {
            if (doc is null) return 0m;

            var cVendor = GetFieldConfidence(doc, "MerchantName") ?? GetFieldConfidence(doc, "Merchant") ?? 0f;
            var cDate = GetFieldConfidence(doc, "TransactionDate") ?? GetFieldConfidence(doc, "Date") ?? 0f;
            var cTotal = GetFieldConfidence(doc, "Total") ?? GetFieldConfidence(doc, "TotalAmount") ?? 0f;
            var cTax = GetFieldConfidence(doc, "TotalTax") ?? GetFieldConfidence(doc, "Tax") ?? 0f;

            decimal score =
                (decimal)cVendor * 0.30m +
                (decimal)cDate * 0.30m +
                (decimal)cTotal * 0.30m +
                (decimal)cTax * 0.10m;

            if (score < 0m) score = 0m;
            if (score > 1m) score = 1m;
            return score;
        }

        private static float? GetFieldConfidence(AnalyzedDocument doc, string fieldName)
        {
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;
            return f.Confidence;
        }

        // ---------------- Field helpers ----------------

        private static string? GetFirstString(AnalyzedDocument? doc, params string[] fieldNames)
        {
            foreach (var name in fieldNames)
            {
                var s = GetString(doc, name);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        private static DateTime? GetFirstDate(AnalyzedDocument? doc, params string[] fieldNames)
        {
            foreach (var name in fieldNames)
            {
                var d = GetDate(doc, name);
                if (d is not null) return d;
            }
            return null;
        }

        private static decimal? GetFirstMoneyAmount(AnalyzedDocument? doc, params string[] fieldNames)
        {
            foreach (var name in fieldNames)
            {
                var m = GetMoneyAmount(doc, name);
                if (m is not null) return m;
            }
            return null;
        }

        private static string? GetFirstCurrency(AnalyzedDocument? doc, params string[] fieldNames)
        {
            foreach (var name in fieldNames)
            {
                var c = GetCurrency(doc, name);
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
            return null;
        }

        private static string? GetString(AnalyzedDocument? doc, string fieldName)
        {
            if (doc is null) return null;
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;
            var s = f.Content?.Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static DateTime? GetDate(AnalyzedDocument? doc, string fieldName)
        {
            if (doc is null) return null;
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;

            try
            {
                return f.Value.AsDate().DateTime;
            }
            catch
            {
                // fall back to parsing the content
            }

            var s = f.Content?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            return DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dt)
                ? dt
                : null;
        }

        private static DateOnly? ParseReceiptDate(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            input = input.Trim();

            var formats = new[]
            {
                "yyyy-MM-dd",
                "MMM dd.yyyy",     // Mar 08.2024
                "MMM dd, yyyy",
                "MM/dd/yyyy",
                "MM/dd/yy",        // 04/04/24 -> 2024-04-04
                "dd/MM/yyyy",
                "dd/MM/yy",
                "yyyy/MM/dd",
                "MM-dd-yyyy",
                "MM-dd-yy"
            };

            if (DateTime.TryParseExact(
                input,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dt))
            {
                // Fix 2-digit year into 2000s if needed
                if (dt.Year is >= 1 and <= 99)
                    dt = new DateTime(dt.Year + 2000, dt.Month, dt.Day);

                return DateOnly.FromDateTime(dt);
            }

            if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt2))
                return DateOnly.FromDateTime(dt2);

            return null;
        }

        private static decimal? GetMoneyAmount(AnalyzedDocument? doc, string fieldName)
        {
            if (doc is null) return null;
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;

            try
            {
                var cur = f.Value.AsCurrency();
                return (decimal)cur.Amount;
            }
            catch
            {
                // fall back to parsing content
            }

            var s = f.Content?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            s = s.Replace(",", "").Trim();
            s = s.Replace("$", "").Replace("CAD", "", StringComparison.OrdinalIgnoreCase).Trim();

            return decimal.TryParse(
                s,
                NumberStyles.Number | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var v)
                ? v
                : null;
        }

        private static string? GetCurrency(AnalyzedDocument? doc, string fieldName)
        {
            if (doc is null) return null;
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;

            try
            {
                var cur = f.Value.AsCurrency();
                return string.IsNullOrWhiteSpace(cur.Code) ? null : cur.Code.Trim();
            }
            catch
            {
                return null;
            }
        }

        // ---------------- NormalizedFieldsJson ----------------

        private string BuildNormalizedFieldsJson(AnalyzedDocument? doc, string currency)
        {
            var merchantName = GetFirstString(doc, "MerchantName", "Merchant", "VendorName");
            var merchantPhone = GetFirstString(doc, "MerchantPhoneNumber");
            var merchantAddress = GetFirstString(doc, "MerchantAddress");
            var receiptType = GetFirstString(doc, "ReceiptType");
            var transactionTime = GetFirstString(doc, "TransactionTime");

            var items = ExtractItems(doc);

            var payload = new
            {
                modelId = _modelId,
                merchantName,
                merchantPhone,
                merchantAddress,
                receiptType,
                transactionTime,
                currency,
                items
            };

            return JsonSerializer.Serialize(payload);
        }

        // ---------------- Tax Fallback (FIXED) ----------------

        private static decimal? TrySumTaxFromText(string? text, decimal? total)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            static bool TryParseMoney(string s, out decimal value)
            {
                value = 0m;
                if (string.IsNullOrWhiteSpace(s)) return false;

                s = s.Trim();
                s = s.Replace(",", "");
                s = s.Replace("$", "", StringComparison.OrdinalIgnoreCase);
                s = s.Replace("CAD", "", StringComparison.OrdinalIgnoreCase).Trim();

                return decimal.TryParse(
                    s,
                    NumberStyles.Number | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out value);
            }

            static bool IsTaxLabel(string line)
            {
                line = line.Trim();
                if (line.Length == 0) return false;

                // common labels; matches "GST (5%)", "PST (ON) (8%)", "HST", "TAX", etc.
                return line.StartsWith("GST", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("HST", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("PST", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("QST", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("VAT", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("TAX", StringComparison.OrdinalIgnoreCase);
            }

            var lines = text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            decimal sum = 0m;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsTaxLabel(line))
                    continue;

                // Case 1: amount on same line ("GST $2.85" / "GST (5%) 2.85")
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in tokens)
                {
                    if (!t.Contains('.', StringComparison.Ordinal)) continue;

                    if (TryParseMoney(t, out var v) && v >= 0)
                    {
                        sum += v;
                        goto Next;
                    }
                }

                // Case 2: amount is next line (common)
                if (i + 1 < lines.Length)
                {
                    var next = lines[i + 1];
                    if (next.Contains('.', StringComparison.Ordinal) && TryParseMoney(next, out var v2) && v2 >= 0)
                    {
                        sum += v2;
                        i += 1; // consume next line
                        continue;
                    }
                }

                // Case 3: amount is 2 lines down (rare)
                if (i + 2 < lines.Length)
                {
                    var next2 = lines[i + 2];
                    if (next2.Contains('.', StringComparison.Ordinal) && TryParseMoney(next2, out var v3) && v3 >= 0)
                    {
                        sum += v3;
                        i += 2;
                        continue;
                    }
                }

            Next:
                continue;
            }

            sum = Math.Round(sum, 2, MidpointRounding.AwayFromZero);
            if (sum <= 0) return null;

            // sanity: tax should not exceed total
            if (total.HasValue && sum > total.Value) return null;

            return sum;
        }

        private static object[] ExtractItems(AnalyzedDocument? doc)
        {
            if (doc is null) return Array.Empty<object>();
            if (!doc.Fields.TryGetValue("Items", out var itemsField) || itemsField is null)
                return Array.Empty<object>();

            IReadOnlyList<DocumentField>? list;
            try { list = itemsField.Value.AsList(); }
            catch { return Array.Empty<object>(); }

            var outItems = new List<object>();

            foreach (var item in list)
            {
                IReadOnlyDictionary<string, DocumentField>? dict;
                try { dict = item.Value.AsDictionary(); }
                catch { continue; }

                dict.TryGetValue("Description", out var desc);
                dict.TryGetValue("Quantity", out var qty);
                dict.TryGetValue("TotalPrice", out var totalPrice);

                outItems.Add(new
                {
                    description = desc?.Content?.Trim(),
                    quantity = qty?.Content?.Trim(),
                    totalPrice = totalPrice?.Content?.Trim(),
                    confidence = item.Confidence
                });
            }

            return outItems.ToArray();
        }
    }
}
