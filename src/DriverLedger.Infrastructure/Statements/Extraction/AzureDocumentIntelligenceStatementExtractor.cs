using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using DriverLedger.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DriverLedger.Infrastructure.Statements.Extraction;

public sealed class AzureDocumentIntelligenceStatementExtractor : IStatementExtractor
{
    private readonly DocumentAnalysisClient _client;
    private readonly string _modelId;

    public string ModelVersion => _modelId;

    public AzureDocumentIntelligenceStatementExtractor(
        IOptions<DocumentIntelligenceOptions> options)
    {
        var o = options.Value;

        _client = new DocumentAnalysisClient(
            new Uri(o.Endpoint),
            new AzureKeyCredential(o.ApiKey));

        _modelId = string.IsNullOrWhiteSpace(o.StatementsModelId)
            ? "prebuilt-invoice"
            : o.StatementsModelId;
    }

    public bool CanHandleContentType(string contentType)
        => contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<StatementLineNormalized>> ExtractAsync(
        Stream content,
        CancellationToken ct)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));

        // Azure DI requires seekable streams
        using var seekable = await EnsureSeekableAsync(content, ct);

        var result = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            _modelId,
            seekable,
            cancellationToken: ct);

        var analyzed = result.Value;
        var doc = analyzed.Documents.FirstOrDefault();

        var lines = new List<StatementLineNormalized>();

        // 1) Try invoice-style Items first
        if (doc != null &&
            TryGetField(doc.Fields, "Items", out var itemsField))
        {
            var parsed = ParseInvoiceItems(itemsField, doc.Fields);
            if (parsed.Count > 0)
                lines.AddRange(parsed);
        }

        // 2) Table parsing
        lines.AddRange(ParseTables(analyzed.Tables, doc?.Fields));

        // 3) Uber fallback from full text
        var uberFallback = UberStatementTextFallback.TryExtract(analyzed.Content);
        if (uberFallback.Count > 0)
            lines.AddRange(uberFallback);

        // Lyft fallback from full text
        var lyftFallback = LyftStatementTextFallback.TryExtract(analyzed.Content);
        if (lyftFallback.Count > 0)
            lines.AddRange(lyftFallback);

        // 4) Dedupe
        // Include IsMetric + MetricKey/Value/Unit + MoneyAmount + CurrencyCode + TaxAmount
        var deduped = lines
            .GroupBy(x =>
                $"{x.LineType}|{x.Description}|{x.IsMetric}|{x.MoneyAmount}|{x.MetricKey}|{x.MetricValue}|{x.Unit}|{x.CurrencyCode}|{x.TaxAmount}")
            .Select(g => g.First())
            .ToList();

        return deduped;
    }

    private static async Task<Stream> EnsureSeekableAsync(Stream input, CancellationToken ct)
    {
        // If it is already seekable, reset to 0 and use it.
        if (input.CanSeek)
        {
            input.Position = 0;
            return input;
        }

        // Otherwise buffer to memory (seekable)
        var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    // ============================================================
    // ✅ THIS is the “normalize before StatementLineNormalized” hook
    // ============================================================
    private static (decimal? MoneyAmount, decimal? TaxAmount, bool IsMetric, string? MetricKey, decimal? MetricValue, string? Unit)
        NormalizeAmountsForLineType(
            string resolvedLineType,
            string? description,
            decimal? amount,
            decimal? taxAmount)
    {
        // 1) Force known metric labels into Metric
        // Example: "Total Rides 467" should NEVER be Income.
        var desc = (description ?? string.Empty).Trim();

        if (desc.Contains("total rides", StringComparison.OrdinalIgnoreCase) ||
            desc.Equals("total rides", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLineType = "Metric";
            return (
                MoneyAmount: null,
                TaxAmount: null,
                IsMetric: true,
                MetricKey: "Trips",
                MetricValue: amount.HasValue ? Math.Abs(amount.Value) : null,
                Unit: "trips"
            );
        }

        // 2) Normalization rules
        if (string.Equals(resolvedLineType, "Metric", StringComparison.OrdinalIgnoreCase))
        {
            // Metric lines: no money/tax
            var metric = ResolveMetricKeyAndUnitLocal(desc);

            return (
                MoneyAmount: null,
                TaxAmount: null,
                IsMetric: true,
                MetricKey: metric.MetricKey,
                MetricValue: amount,
                Unit: metric.Unit
            );
        }

        if (string.Equals(resolvedLineType, "TaxCollected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resolvedLineType, "Itc", StringComparison.OrdinalIgnoreCase))
        {
            // Tax-only lines: store amount in TaxAmount ONLY
            // IMPORTANT: we do NOT want this to hit MoneyAmount.
            var v = taxAmount ?? amount;
            return (
                MoneyAmount: null,
                TaxAmount: v.HasValue && v.Value != 0m ? Math.Abs(v.Value) : null,
                IsMetric: false,
                MetricKey: null,
                MetricValue: null,
                Unit: null
            );
        }

        // Monetary lines: store amount in MoneyAmount ONLY
        return (
            MoneyAmount: amount.HasValue ? Math.Abs(amount.Value) : null,
            TaxAmount: null,
            IsMetric: false,
            MetricKey: null,
            MetricValue: null,
            Unit: null
        );
    }

    private static List<StatementLineNormalized> ParseInvoiceItems(
        DocumentField itemsField,
        IReadOnlyDictionary<string, DocumentField> docFields)
    {
        var lines = new List<StatementLineNormalized>();

        if (!TryAsList(itemsField, out var items))
            return lines;

        var docDate =
            TryGetDate(docFields, "InvoiceDate") ??
            TryGetDate(docFields, "StatementDate") ??
            DateOnly.MinValue;

        // Optional statement-level currency hint
        var statementLevelCurrency =
            TryGetString(docFields, "Currency") ??
            TryGetString(docFields, "CurrencyCode") ??
            TryGetString(docFields, "InvoiceCurrency");

        foreach (var item in items)
        {
            if (!TryAsDictionary(item, out var dict))
                continue;

            var description =
                StatementExtractionParsing.FirstNonEmpty(
                    TryGetString(dict, "Description"),
                    TryGetString(dict, "Name"),
                    TryGetString(dict, "Item"));

            var rawAmountText = FindMoneyField(dict, "Amount", "Total", "Value", "Net");
            var rawTaxText = FindTaxField(dict);

            var amount = StatementExtractionParsing.ParseAmount(rawAmountText);
            var tax = StatementExtractionParsing.ParseAmount(rawTaxText);


            var resolvedLineType = StatementExtractionParsing.ResolveLineType(
                rawType: null,
                amount: amount,
                taxAmount: tax,
                description: description);

            // Currency normalization + evidence
            var (currencyCode, currencyEvidence) = StatementExtractionParsing.ResolveCurrencyCode(
                lineCurrencyCell: TryGetString(dict, "Currency"),
                statementLevelCurrency: statementLevelCurrency,
                amountCell: rawAmountText);

            var classificationEvidence =
                StatementExtractionParsing.ResolveClassificationEvidence(rawType: null, resolvedLineType);

            // ✅ APPLY NORMALIZATION RIGHT HERE (before record creation)
            var normalized = NormalizeAmountsForLineType(
                resolvedLineType,
                description,
                amount,
                tax);

            lines.Add(new StatementLineNormalized(
                LineDate: docDate,
                LineType: resolvedLineType,
                Description: description ?? "Statement item",

                CurrencyCode: currencyCode,
                CurrencyEvidence: currencyEvidence,

                ClassificationEvidence: classificationEvidence,

                IsMetric: normalized.IsMetric,
                MetricKey: normalized.MetricKey,
                MetricValue: normalized.MetricValue,
                Unit: normalized.Unit,

                MoneyAmount: normalized.MoneyAmount,
                TaxAmount: normalized.TaxAmount
            ));
        }

        return lines;
    }

    private static List<StatementLineNormalized> ParseTables(
        IReadOnlyList<DocumentTable> tables,
        IReadOnlyDictionary<string, DocumentField>? fields)
    {
        var lines = new List<StatementLineNormalized>();

        foreach (var table in tables)
        {
            var rows = table.Cells.GroupBy(c => c.RowIndex);

            foreach (var row in rows)
            {
                var cells = row
                    .OrderBy(c => c.ColumnIndex)
                    .Select(c => c.Content?.Trim() ?? "")
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (cells.Count == 0)
                    continue;

                var rowText = string.Join(" ", cells);

                // 1) metric detection first
                if (StatementExtractionParsing.TryParseRideDistanceMetric(rowText, out var mk, out var mv, out var unit))
                {
                    lines.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "Metric",
                        Description: rowText,

                        CurrencyCode: null,
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",

                        IsMetric: true,
                        MetricKey: mk,
                        MetricValue: mv,
                        Unit: unit,

                        MoneyAmount: null,
                        TaxAmount: null
                    ));
                    continue;
                }

                // 2) monetary parse: take last cell as amount candidate
                var amountText = cells.Last();
                var amount = StatementExtractionParsing.ParseAmount(amountText);
                if (!amount.HasValue) continue;

                var currency = StatementExtractionParsing.ExtractCurrency(rowText) ??
                               StatementExtractionParsing.ExtractCurrency(amountText);

                // Optional: try parse a tax cell if present
                // (some tables have "... | Tax | Amount" style layouts)
                decimal? taxAmount = null;
                if (cells.Count >= 2)
                {
                    // heuristically check second-last cell
                    var maybeTaxText = cells[^2];
                    var maybeTax = StatementExtractionParsing.ParseAmount(maybeTaxText);
                    // only treat it as tax if it looks small-ish and row mentions gst/hst
                    if (maybeTax.HasValue &&
                        (rowText.Contains("gst", StringComparison.OrdinalIgnoreCase) ||
                         rowText.Contains("hst", StringComparison.OrdinalIgnoreCase) ||
                         rowText.Contains("tax", StringComparison.OrdinalIgnoreCase)))
                    {
                        taxAmount = maybeTax;
                    }
                }

                var resolvedLineType = StatementExtractionParsing.ResolveLineType(
                    rawType: null,
                    amount: amount,
                    taxAmount: taxAmount,
                    description: rowText);

                //  APPLY NORMALIZATION 
                var normalized = NormalizeAmountsForLineType(
                    resolvedLineType,
                    rowText,
                    amount,
                    taxAmount);

                lines.Add(new StatementLineNormalized(
                    LineDate: DateOnly.MinValue,
                    LineType: resolvedLineType,
                    Description: rowText,

                    CurrencyCode: currency,
                    CurrencyEvidence: currency is null ? "Inferred" : "Extracted",
                    ClassificationEvidence: "Inferred",

                    IsMetric: normalized.IsMetric,
                    MetricKey: normalized.MetricKey,
                    MetricValue: normalized.MetricValue,
                    Unit: normalized.Unit,

                    MoneyAmount: normalized.MoneyAmount,
                    TaxAmount: normalized.TaxAmount
                ));
            }
        }

        return lines;
    }

    // === helpers ===

    private static bool TryGetField(
        IReadOnlyDictionary<string, DocumentField> dict,
        string key,
        out DocumentField field)
    {
        if (dict.TryGetValue(key, out var f) && f is not null)
        {
            field = f;
            return true;
        }

        field = default!;
        return false;
    }

    private static bool TryAsList(
        DocumentField field,
        out IReadOnlyList<DocumentField> list)
    {
        try
        {
            list = field.Value.AsList();
            return true;
        }
        catch
        {
            list = Array.Empty<DocumentField>();
            return false;
        }
    }

    private static bool TryAsDictionary(
        DocumentField field,
        out IReadOnlyDictionary<string, DocumentField> dict)
    {
        try
        {
            dict = field.Value.AsDictionary();
            return true;
        }
        catch
        {
            dict = new Dictionary<string, DocumentField>();
            return false;
        }
    }


    private static string? TryGetString(
        IReadOnlyDictionary<string, DocumentField> dict,
        string key)
        => TryGetField(dict, key, out var f) ? f.Content : null;

    private static DateOnly? TryGetDate(
        IReadOnlyDictionary<string, DocumentField> dict,
        string key)
    {
        if (!TryGetField(dict, key, out var f)) return null;
        return StatementExtractionParsing.ParseDate(f.Content);
    }
    private static (string? MetricKey, string? Unit) ResolveMetricKeyAndUnitLocal(string? description)
    {
        var desc = (description ?? string.Empty).Trim().ToLowerInvariant();

        // trips / total rides
        if (desc.Contains("total rides") || (desc.Contains("rides") && desc.Contains("total")))
            return ("Trips", "trips");

        if (desc.Contains("trip") && (desc.Contains("count") || desc.Contains("total")))
            return ("Trips", "trips");

        // distance
        if (desc.Contains("kilomet") || desc.Contains(" km"))
            return ("RideKilometers", "km");

        if (desc.Contains(" mile") || desc.Contains(" mi"))
            return ("RideMiles", "mi");

        // online hours, etc (optional)
        if (desc.Contains("online hour") || desc.Contains("hours online") || desc.Contains("online time"))
            return ("OnlineHours", "hours");

        return (null, null);
    }

    private static string? FindMoneyField(
        IReadOnlyDictionary<string, DocumentField> dict,
        params string[] preferredKeys)
    {
        // 1) Exact preferred keys
        foreach (var k in preferredKeys)
        {
            var v = TryGetString(dict, k);
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }

        // 2) Heuristic: keys containing these tokens
        var tokens = new[] { "amount", "total", "value", "tax", "gst", "hst" };

        foreach (var kvp in dict)
        {
            var key = kvp.Key ?? string.Empty;
            if (tokens.Any(t => key.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                // Only call TryGetString if key is not null or empty
                if (!string.IsNullOrEmpty(key))
                {
                    var v = TryGetString(dict, key);
                    if (!string.IsNullOrWhiteSpace(v))
                        return v;
                }
            }
        }

        // 3) Last resort: any field that parses as an amount
        foreach (var kvp in dict)
        {
            var key = kvp.Key;
            if (string.IsNullOrEmpty(key)) continue;

            var v = TryGetString(dict, key);
            if (string.IsNullOrWhiteSpace(v)) continue;

            if (StatementExtractionParsing.ParseAmount(v).HasValue)
                return v;
        }

        return null;
    }

    private static string? FindTaxField(IReadOnlyDictionary<string, DocumentField> dict)
    {
        // Try common DI variants first
        return FindMoneyField(dict,
            "Tax",
            "TaxAmount",
            "GST",
            "HST",
            "GST/HST",
            "GSTHST",
            "SalesTax");
    }


}
