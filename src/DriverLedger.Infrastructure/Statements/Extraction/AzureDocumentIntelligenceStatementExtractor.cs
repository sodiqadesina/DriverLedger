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

            // Prefer ParseAmount so we don’t treat junk as 0.
            var rawAmountText = TryGetString(dict, "Amount");
            var rawTaxText = TryGetString(dict, "Tax");
            var amount = StatementExtractionParsing.ParseAmount(rawAmountText);
            var tax = StatementExtractionParsing.ParseAmount(rawTaxText);

            var resolvedLineType = StatementExtractionParsing.ResolveLineType(
                rawType: null,
                amount: amount,
                taxAmount: tax,
                description: description);

            var isMetric = string.Equals(resolvedLineType, "Metric", StringComparison.OrdinalIgnoreCase);

            var (metricKey, unit) = isMetric
                ? StatementExtractionParsing.ResolveMetricKeyAndUnit(null, description)
                : (null, null);

            // Currency normalization + evidence
            var (currencyCode, currencyEvidence) = StatementExtractionParsing.ResolveCurrencyCode(
                lineCurrencyCell: TryGetString(dict, "Currency"),
                statementLevelCurrency: statementLevelCurrency,
                amountCell: rawAmountText);

            var classificationEvidence =
                StatementExtractionParsing.ResolveClassificationEvidence(rawType: null, resolvedLineType);

            // Money vs Metric value
            decimal? moneyAmount = null;
            decimal? metricValue = null;

            if (isMetric)
            {
                // Metric rows should not carry money into ledger.
                // For invoice items, metric value might be embedded in Amount cell or description.
                metricValue = amount;
            }
            else
            {
                // Only monetary line types carry MoneyAmount
                // Keep your old convention: amounts stored as abs (sign not important here).
                moneyAmount = amount.HasValue ? Math.Abs(amount.Value) : null;
            }

            lines.Add(new StatementLineNormalized(
                LineDate: docDate,
                LineType: resolvedLineType,
                Description: description ?? "Statement item",

                IsMetric: isMetric,
                MoneyAmount: moneyAmount,
                MetricValue: metricValue,
                MetricKey: metricKey,
                Unit: unit,

                CurrencyCode: currencyCode,
                CurrencyEvidence: currencyEvidence,
                ClassificationEvidence: classificationEvidence,

                TaxAmount: tax.HasValue && tax.Value != 0m ? Math.Abs(tax.Value) : null
            ));
        }

        return lines;
    }

    private static List<StatementLineNormalized> ParseTables(
    IReadOnlyList<DocumentTable> tables, IReadOnlyDictionary<string, DocumentField>? fields)
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
                var money = StatementExtractionParsing.ParseAmount(amountText);

                if (!money.HasValue) continue;

                var currency = StatementExtractionParsing.ExtractCurrency(rowText) ??
                               StatementExtractionParsing.ExtractCurrency(amountText);

                var lineType = StatementExtractionParsing.ResolveLineType(
                    rawType: null,
                    amount: money.Value,
                    taxAmount: null,
                    description: rowText);

                lines.Add(new StatementLineNormalized(
                    LineDate: DateOnly.MinValue,
                    LineType: lineType,
                    Description: rowText,
                    CurrencyCode: currency,
                    CurrencyEvidence: currency is null ? "Inferred" : "Extracted",
                    ClassificationEvidence: "Inferred",
                    IsMetric: false,
                    MetricKey: null,
                    MetricValue: null,
                    Unit: null,
                    MoneyAmount: Math.Abs(money.Value),
                    TaxAmount: null
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
}
