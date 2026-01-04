using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Files;
using DriverLedger.Domain.Notifications;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public sealed class StatementExtractionHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IBlobStorage blobStorage,
        IEnumerable<IStatementExtractor> extractors,
        IMessagePublisher publisher,
        ILogger<StatementExtractionHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task HandleAsync(MessageEnvelope<StatementReceived> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var msg = envelope.Data;

            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["fileObjectId"] = msg.FileObjectId,
                ["provider"] = msg.Provider,
                ["periodKey"] = msg.PeriodKey
            });

            // Load file metadata
            var file = await db.FileObjects.SingleAsync(x => x.TenantId == tenantId && x.Id == msg.FileObjectId, ct);

            // Choose extractor by content type
            var extractor = extractors.FirstOrDefault(x => x.CanHandleContentType(file.ContentType));
            if (extractor is null)
                throw new InvalidOperationException($"No statement extractor found for content type: {file.ContentType}");

            // Open blob stream
            await using var content = await blobStorage.OpenReadAsync(file.BlobPath, ct);

            // Extract normalized lines
            var normalized = await extractor.ExtractAsync(content, ct);

            // Determine statement-level currency (once)
            var resolvedStatementCurrency = ResolveStatementCurrency(file, normalized, out var stmtCurrencyEvidence);

            // Upsert statement (by Provider/PeriodType/PeriodKey unique index)
            var statement = await db.Statements.SingleOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.Provider == msg.Provider &&
                x.PeriodType == msg.PeriodType &&
                x.PeriodKey == msg.PeriodKey, ct);

            if (statement is null)
            {
                statement = new Statement
                {
                    TenantId = tenantId,
                    FileObjectId = msg.FileObjectId,
                    Provider = msg.Provider,
                    PeriodType = msg.PeriodType,
                    PeriodKey = msg.PeriodKey,
                    PeriodStart = msg.PeriodStart,
                    PeriodEnd = msg.PeriodEnd,
                    VendorName = msg.Provider,
                    Status = "Draft",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Statements.Add(statement);
            }
            else
            {
                // update file link + period boundaries if reprocessing
                statement.FileObjectId = msg.FileObjectId;
                statement.PeriodStart = msg.PeriodStart;
                statement.PeriodEnd = msg.PeriodEnd;
                statement.Status = "Draft";
            }

            statement.CurrencyCode = resolvedStatementCurrency;
            statement.CurrencyEvidence = stmtCurrencyEvidence;

            // Replace existing lines for idempotency
            var existingLines = await db.StatementLines
                .Where(x => x.TenantId == tenantId && x.StatementId == statement.Id)
                .ToListAsync(ct);

            db.StatementLines.RemoveRange(existingLines);

            // Persist normalized -> domain
            foreach (var n in normalized)
            {
                // normalize per-line currency (inherit statement if missing)
                var lineCurrency = n.CurrencyCode ?? statement.CurrencyCode;
                var lineCurrencyEvidence = n.CurrencyCode is null ? "Inferred" : n.CurrencyEvidence;

                // enforce money only for Income/Fee/Expense
                decimal? moneyAmount = null;
                if (!n.IsMetric && StatementExtractionParsing.IsMonetaryType(n.LineType))
                    moneyAmount = n.MoneyAmount;

                // tax-only lines keep money null, tax populated
                if (!n.IsMetric && StatementExtractionParsing.IsTaxOnlyType(n.LineType))
                    moneyAmount = null;

                var line = new StatementLine
                {
                    TenantId = tenantId,
                    StatementId = statement.Id,
                    LineDate = n.LineDate,

                    LineType = n.IsMetric ? "Metric" : n.LineType,
                    Description = n.Description,

                    CurrencyCode = lineCurrency,
                    CurrencyEvidence = lineCurrencyEvidence,

                    ClassificationEvidence = n.ClassificationEvidence,

                    IsMetric = n.IsMetric,
                    MetricKey = n.MetricKey,
                    MetricValue = n.MetricValue,
                    Unit = n.Unit,

                    MoneyAmount = moneyAmount,
                    TaxAmount = n.TaxAmount
                };

                db.StatementLines.Add(line);
            }

            // compute rollups for event payload
            var monetary = normalized.Where(x => !x.IsMetric && StatementExtractionParsing.IsMonetaryType(x.LineType)).ToList();
            var incomeTotal = monetary.Where(x => x.LineType == "Income").Sum(x => x.MoneyAmount ?? 0m);
            var feeTotal = monetary.Where(x => x.LineType == "Fee").Sum(x => x.MoneyAmount ?? 0m);
            var taxTotal = normalized.Where(x => !x.IsMetric).Sum(x => x.TaxAmount ?? 0m);

            statement.StatementTotalAmount = incomeTotal - feeTotal; // simple net; adjust later if you want
            statement.TaxAmount = taxTotal;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                ActorUserId = "system",
                Action = "statement.parsed",
                EntityType = "Statement",
                EntityId = statement.Id.ToString("D"),
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = statement.Provider,
                    periodType = statement.PeriodType,
                    periodKey = statement.PeriodKey,
                    lineCount = normalized.Count,
                    statementCurrency = statement.CurrencyCode,
                    currencyEvidence = statement.CurrencyEvidence,
                    modelVersion = extractor.ModelVersion
                }, JsonOpts)
            });

            db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                Type = "StatementParsed",
                Severity = "Info",
                Title = "Statement parsed",
                Body = $"Parsed {normalized.Count} lines ({statement.Provider} {statement.PeriodKey}).",
                DataJson = JsonSerializer.Serialize(new { statementId = statement.Id }, JsonOpts),
                Status = "New",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);

            // Publish StatementParsed (constructor signature matters!)
            var parsed = new StatementParsed(
                StatementId: statement.Id,
                TenantId: tenantId,
                Provider: statement.Provider,
                PeriodType: statement.PeriodType,
                PeriodKey: statement.PeriodKey,
                LineCount: normalized.Count,
                IncomeTotal: incomeTotal,
                FeeTotal: feeTotal,
                TaxTotal: taxTotal
            );

            var outEnvelope = new MessageEnvelope<StatementParsed>(
                MessageId: Guid.NewGuid().ToString("N"),
                Type: "statement.parsed.v1",
                OccurredAt: DateTimeOffset.UtcNow,
                TenantId: tenantId,
                CorrelationId: correlationId,
                Data: parsed
            );

            await publisher.PublishAsync("q.statement.parsed", outEnvelope, ct);
        }

        private static string ResolveStatementCurrency(
            FileObject file,
            IReadOnlyList<StatementLineNormalized> lines,
            out string evidence)
        {
            // 1) try from lines explicitly
            var explicitCur = lines
                .Select(x => x.CurrencyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(explicitCur))
            {
                evidence = "Extracted";
                return explicitCur!;
            }

            // 2) Canada-first fallback
            evidence = "Inferred";
            return "CAD";
        }
    }
}
