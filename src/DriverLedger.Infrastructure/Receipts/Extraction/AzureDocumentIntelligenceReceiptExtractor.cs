
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

            DateOnly? date = null;
            var dt = GetFirstDate(doc, "TransactionDate", "Date");
            if (dt is not null)
                date = DateOnly.FromDateTime(dt.Value);

            var total = GetFirstMoneyAmount(doc, "Total", "TotalAmount", "Amount");
            var tax = GetFirstMoneyAmount(doc, "TotalTax", "Tax", "TaxAmount");

            // Currency: do NOT silently assume CAD in the extractor; preserve “unknown”
            var currency =
                GetFirstCurrency(doc, "Total", "TotalAmount", "TotalTax", "Tax")
                ?? "CAD"; // If your app is Canada-only, this default is OK. Otherwise, consider leaving null + policy layer.

            // DI confidence only (policy confidence stays in ReceiptConfidenceCalculator)
            var diConfidence = ComputeDiConfidence(doc);

            var normalizedFieldsJson = BuildNormalizedFieldsJson(doc, currency);

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
            // Keep small + stable: enough for audit/debugging without serialization pitfalls
            var projection = new
            {
                modelId = _modelId,
                createdOn = DateTime.UtcNow,
                docCount = result.Documents.Count,
                pageCount = result.Pages.Count,
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

            // Weighted on critical posting fields
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
                DateTimeStyles.AssumeLocal,
                out var dt)
                ? dt
                : null;
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
