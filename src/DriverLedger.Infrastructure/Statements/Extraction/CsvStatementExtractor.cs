

using System.Text;

namespace DriverLedger.Infrastructure.Statements.Extraction

{
    public sealed class CsvStatementExtractor : IStatementExtractor
    {
        public string ModelVersion => "csv";

        public bool CanHandleContentType(string contentType)
            => contentType.Contains("csv", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);

        public async Task<IReadOnlyList<StatementLineNormalized>> ExtractAsync(Stream file, CancellationToken ct)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            if (file.CanSeek) file.Position = 0;

            using var reader = new StreamReader(file, Encoding.UTF8, leaveOpen: true);
            var lines = new List<string>();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
                return Array.Empty<StatementLineNormalized>();

            var firstRow = ParseCsvLine(lines[0]);
            var looksLikeHeader = firstRow.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                (value.Contains("date", StringComparison.OrdinalIgnoreCase)
                 || value.Contains("amount", StringComparison.OrdinalIgnoreCase)
                 || value.Contains("description", StringComparison.OrdinalIgnoreCase)));

            var dataStart = looksLikeHeader ? 1 : 0;

            int? dateCol = looksLikeHeader ? FindHeaderColumn(firstRow, "date") : 0;
            int? descCol = looksLikeHeader ? FindHeaderColumn(firstRow, "description", "details", "memo") : 1;
            int? typeCol = looksLikeHeader ? FindHeaderColumn(firstRow, "type", "category", "line type") : 4;
            int? amountCol = looksLikeHeader ? FindHeaderColumn(firstRow, "amount", "total", "net", "gross") : 2;
            int? taxCol = looksLikeHeader ? FindHeaderColumn(firstRow, "tax", "gst", "hst", "vat") : 3;
            int? currencyCol = looksLikeHeader ? FindHeaderColumn(firstRow, "currency", "curr") : 5;

            var output = new List<StatementLineNormalized>();

            for (var i = dataStart; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var row = ParseCsvLine(lines[i]);

                string? GetCell(int? col)
                    => col.HasValue && col.Value < row.Count ? row[col.Value] : null;

                var dateText = GetCell(dateCol);
                var descText = GetCell(descCol);
                var typeText = GetCell(typeCol);
                var amountText = GetCell(amountCol);
                var taxText = GetCell(taxCol);
                var currencyText = GetCell(currencyCol);

                var amount = StatementExtractionParsing.ParseAmount(amountText);
                var taxAmount = StatementExtractionParsing.ParseAmount(taxText);

                if (!amount.HasValue && !taxAmount.HasValue) continue;

                var lineDate = StatementExtractionParsing.ParseDate(dateText) ?? DateOnly.MinValue;
                var currency = StatementExtractionParsing.FirstNonEmpty(
                    StatementExtractionParsing.ExtractCurrency(currencyText),
                    StatementExtractionParsing.ExtractCurrency(amountText),
                    StatementExtractionParsing.ExtractCurrency(taxText));

                var lineType = StatementExtractionParsing.ResolveLineType(typeText, amount, taxAmount);
                var description = StatementExtractionParsing.FirstNonEmpty(descText, typeText);

                output.Add(new StatementLineNormalized(
                    lineDate,
                    lineType,
                    description,
                    currency,
                    amount ?? 0m,
                    taxAmount));
            }

            return output;
        }

        private static int? FindHeaderColumn(IReadOnlyList<string> headers, params string[] keywords)
        {
            for (var i = 0; i < headers.Count; i++)
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

        private static List<string> ParseCsvLine(string line)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(line)) return results;

            var sb = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    results.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            results.Add(sb.ToString().Trim());
            return results;
        }
    }
}
