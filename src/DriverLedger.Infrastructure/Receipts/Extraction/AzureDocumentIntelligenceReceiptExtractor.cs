
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
        private const string ModelId = "prebuilt-receipt";

        private readonly DocumentAnalysisClient _client;
        private readonly ILogger<AzureDocumentIntelligenceReceiptExtractor> _logger;

        public string ModelVersion => ModelId;

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

            _client = new DocumentAnalysisClient(new Uri(o.Endpoint), new AzureKeyCredential(o.ApiKey));
        }

        public async Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (file.CanSeek) file.Position = 0;

            _logger.LogInformation("Analyzing receipt via Azure Document Intelligence ({ModelId})", ModelId);

            AnalyzeDocumentOperation op = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                ModelId,
                file,
                cancellationToken: ct);

            AnalyzeResult result = op.Value;
            var doc = result.Documents.FirstOrDefault();

            // 1) RawJson for audit
            var rawJson = JsonSerializer.Serialize(result);

            // 2) Core normalized fields used for posting & HOLD rules
            var vendor = GetString(doc, "MerchantName")
                         ?? GetString(doc, "MerchantAddress") // fallback
                         ?? null;

            DateOnly? date = null;
            var dt = GetDate(doc, "TransactionDate");
            if (dt is not null)
                date = DateOnly.FromDateTime(dt.Value);

            var total = GetMoneyAmount(doc, "Total");
            var tax = GetMoneyAmount(doc, "TotalTax");

            var currency = GetCurrency(doc, "Total")
                           ?? GetCurrency(doc, "TotalTax")
                           ?? "CAD";

            var confidence = ComputeConfidence(doc);

            // 3) Extended normalized JSON (future-proofing)
            var normalizedFields = BuildNormalizedFieldsJson(doc, currency);
            var normalizedFieldsJson = JsonSerializer.Serialize(normalizedFields);

            // IMPORTANT: match record positional order exactly
            return new NormalizedReceipt(
                date,
                vendor,
                total,
                tax,
                currency,
                confidence,
                rawJson,
                normalizedFieldsJson
            );
        }

        // ---------------- Helpers ----------------

        private static decimal ComputeConfidence(AnalyzedDocument? doc)
        {
            if (doc is null) return 0m;

            // weighted by what's critical for posting
            var cVendor = GetFieldConfidence(doc, "MerchantName");
            var cDate = GetFieldConfidence(doc, "TransactionDate");
            var cTotal = GetFieldConfidence(doc, "Total");
            var cTax = GetFieldConfidence(doc, "TotalTax"); // lower weight

            decimal score =
                (decimal)cVendor * 0.30m +
                (decimal)cDate * 0.30m +
                (decimal)cTotal * 0.30m +
                (decimal)cTax * 0.10m;

            // clamp
            if (score < 0m) score = 0m;
            if (score > 1m) score = 1m;
            return score;
        }

        private static float GetFieldConfidence(AnalyzedDocument doc, string fieldName)
        {
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null)
                return 0f;

            return f.Confidence ?? 0f;
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

            // Version-safe: try typed parse first
            try
            {
               
                return f.Value.AsDate().DateTime;
            }
            catch
            {
                // ignore and fall back
            }

            // Conservative fallback: try parse from content
            var s = f.Content?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            // Many receipts are yyyy-MM-dd or similar; try a broad parse
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
                ? dt
                : null;
        }


        private static decimal? GetMoneyAmount(AnalyzedDocument? doc, string fieldName)
        {
            if (doc is null) return null;
            if (!doc.Fields.TryGetValue(fieldName, out var f) || f is null) return null;

            // Version-safe: try typed currency
            try
            {
                var cur = f.Value.AsCurrency();
                return (decimal)cur.Amount;
            }
            catch
            {
                // ignore and fall back
            }

            // Fallback: parse raw content
            var s = f.Content?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;

            s = s.Replace(",", "").Trim();
            s = s.Replace("$", "").Replace("CAD", "", StringComparison.OrdinalIgnoreCase).Trim();

            return decimal.TryParse(
                s,
                NumberStyles.Number | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var v
            ) ? v : null;
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


        private static object BuildNormalizedFieldsJson(AnalyzedDocument? doc, string currency)
        {
            // Keep this stable; your downstream can evolve without breaking the record
            var merchantName = GetString(doc, "MerchantName");
            var merchantPhone = GetString(doc, "MerchantPhoneNumber");
            var merchantAddress = GetString(doc, "MerchantAddress");
            var receiptType = GetString(doc, "ReceiptType");
            var transactionTime = GetString(doc, "TransactionTime");

            var items = ExtractItems(doc);

            return new
            {
                modelId = ModelId,
                merchantName,
                merchantPhone,
                merchantAddress,
                receiptType,
                transactionTime,
                currency,
                items
            };
        }

        private static object[] ExtractItems(AnalyzedDocument? doc)
        {
            if (doc is null) return Array.Empty<object>();
            if (!doc.Fields.TryGetValue("Items", out var itemsField) || itemsField is null)
                return Array.Empty<object>();

            // Version-safe: attempt AsList without checking ValueType
            IReadOnlyList<DocumentField>? list;
            try
            {
                list = itemsField.Value.AsList();
            }
            catch
            {
                return Array.Empty<object>();
            }

            var outItems = new List<object>();

            foreach (var item in list)
            {
                // Each item is typically a dictionary
                IReadOnlyDictionary<string, DocumentField>? dict;
                try
                {
                    dict = item.Value.AsDictionary();
                }
                catch
                {
                    continue;
                }

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
