

using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using DriverLedger.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverLedger.Infrastructure.Statements.Extraction

{
    public sealed class AzureDocumentIntelligenceStatementExtractor : IStatementExtractor
    {
        private readonly DocumentAnalysisClient _client;
        private readonly ILogger<AzureDocumentIntelligenceStatementExtractor> _logger;
        private readonly string _modelId;

        public string ModelVersion => _modelId;

        public AzureDocumentIntelligenceStatementExtractor(
            IOptions<DocumentIntelligenceOptions> options,
            ILogger<AzureDocumentIntelligenceStatementExtractor> logger)
        {
            _logger = logger;

            var o = options.Value;

            if (string.IsNullOrWhiteSpace(o.Endpoint))
                throw new InvalidOperationException("Azure:DocumentIntelligence:Endpoint is missing.");
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                throw new InvalidOperationException("Azure:DocumentIntelligence:ApiKey is missing.");

            _modelId = string.IsNullOrWhiteSpace(o.ModelId) ? "prebuilt-layout" : o.ModelId.Trim();

            _client = new DocumentAnalysisClient(new Uri(o.Endpoint), new AzureKeyCredential(o.ApiKey));
        }

        public bool CanHandleContentType(string contentType)
            => contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

        public async Task<IReadOnlyList<StatementLineNormalized>> ExtractAsync(Stream file, CancellationToken ct)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (file.CanSeek) file.Position = 0;

            _logger.LogInformation("Analyzing statement via Azure Document Intelligence ({ModelId})", _modelId);

            AnalyzeDocumentOperation op = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                file,
                cancellationToken: ct);

            AnalyzeResult result = op.Value;

            var lines = new List<StatementLineNormalized>();

            foreach (var table in result.Tables)
            {
                lines.AddRange(ParseTable(table));
            }

            if (lines.Count == 0)
            {
                _logger.LogWarning("No statement lines detected from Document Intelligence output.");
            }

            return lines;
        }

        private static IEnumerable<StatementLineNormalized> ParseTable(DocumentTable table)
        {
            if (table.RowCount == 0 || table.ColumnCount == 0)
                yield break;

            var cellLookup = table.Cells
                .GroupBy(c => (c.RowIndex, c.ColumnIndex))
                .ToDictionary(g => g.Key, g => g.First().Content?.Trim());

            string? GetCell(int row, int col)
                => cellLookup.TryGetValue((row, col), out var value) ? value : null;

            var headerRow = Enumerable.Range(0, table.ColumnCount)
                .Select(col => GetCell(0, col))
                .ToArray();

            bool looksLikeHeader = headerRow.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                (value.Contains("date", StringComparison.OrdinalIgnoreCase)
                 || value.Contains("amount", StringComparison.OrdinalIgnoreCase)
                 || value.Contains("description", StringComparison.OrdinalIgnoreCase)
                 || value.Contains("type", StringComparison.OrdinalIgnoreCase)));

            var dataStartRow = looksLikeHeader ? 1 : 0;

            int? dateCol = looksLikeHeader ? FindHeaderColumn(headerRow, "date") : 0;
            int? descCol = looksLikeHeader ? FindHeaderColumn(headerRow, "description", "details", "memo") : 1;
            int? typeCol = looksLikeHeader ? FindHeaderColumn(headerRow, "type", "category", "line type") : 4;
            int? amountCol = looksLikeHeader ? FindHeaderColumn(headerRow, "amount", "total", "net", "gross") : 2;
            int? taxCol = looksLikeHeader ? FindHeaderColumn(headerRow, "tax", "gst", "hst", "vat") : 3;
            int? currencyCol = looksLikeHeader ? FindHeaderColumn(headerRow, "currency", "curr") : 5;

            for (var row = dataStartRow; row < table.RowCount; row++)
            {
                var dateText = dateCol.HasValue ? GetCell(row, dateCol.Value) : null;
                var descText = descCol.HasValue ? GetCell(row, descCol.Value) : null;
                var typeText = typeCol.HasValue ? GetCell(row, typeCol.Value) : null;
                var amountText = amountCol.HasValue ? GetCell(row, amountCol.Value) : null;
                var taxText = taxCol.HasValue ? GetCell(row, taxCol.Value) : null;
                var currencyText = currencyCol.HasValue ? GetCell(row, currencyCol.Value) : null;

                var amount = StatementExtractionParsing.ParseAmount(amountText);
                var taxAmount = StatementExtractionParsing.ParseAmount(taxText);

                if (!amount.HasValue && !taxAmount.HasValue)
                {
                    continue;
                }

                var lineDate = StatementExtractionParsing.ParseDate(dateText) ?? DateOnly.MinValue;
                var currency = StatementExtractionParsing.FirstNonEmpty(
                    StatementExtractionParsing.ExtractCurrency(currencyText),
                    StatementExtractionParsing.ExtractCurrency(amountText),
                    StatementExtractionParsing.ExtractCurrency(taxText));

                var lineType = StatementExtractionParsing.ResolveLineType(typeText, amount, taxAmount);
                var description = StatementExtractionParsing.FirstNonEmpty(descText, typeText);

                yield return new StatementLineNormalized(
                    LineDate: lineDate,
                    LineType: lineType,
                    Description: description,
                    Currency: currency,
                    Amount: amount ?? 0m,
                    TaxAmount: taxAmount
                );
            }
        }

        private static int? FindHeaderColumn(string?[] headers, params string[] keywords)
        {
            for (var i = 0; i < headers.Length; i++)
            {
                var header = headers[i];
                if (string.IsNullOrWhiteSpace(header)) continue;

                foreach (var keyword in keywords)
                {
                    if (header.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return null;
        }
    }

}
