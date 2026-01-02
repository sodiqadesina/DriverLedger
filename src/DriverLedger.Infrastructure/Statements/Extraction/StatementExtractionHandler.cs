

using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Ops;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DriverLedger.Infrastructure.Statements.Extraction
{

    public sealed class StatementExtractionHandler(
    DriverLedgerDbContext db,
    ITenantProvider tenantProvider,
    IEnumerable<IStatementExtractor> extractors,
    IBlobStorage blobStorage,
    IMessagePublisher publisher,
    ILogger<StatementExtractionHandler> logger)
    {
        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            if (ex.InnerException is not Microsoft.Data.SqlClient.SqlException sql) return false;
            return sql.Number is 2601 or 2627;
        }

        public async Task HandleAsync(MessageEnvelope<StatementReceived> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var payload = envelope.Data;

            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["statementId"] = payload.StatementId
            });

            var dedupeKey = $"statement.extract:{payload.StatementId}";

            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "statement.extract" && x.DedupeKey == dedupeKey, ct);

            if (existingJob is not null && existingJob.Status == "Succeeded")
            {
                logger.LogInformation("Extraction already succeeded. DedupeKey={DedupeKey}", dedupeKey);
                return;
            }

            ProcessingJob job;
            if (existingJob is null)
            {
                job = new()
                {
                    TenantId = tenantId,
                    JobType = "statement.extract",
                    DedupeKey = dedupeKey,
                    Status = "Started",
                    Attempts = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.ProcessingJobs.Add(job);
            }
            else
            {
                job = existingJob;
                job.Attempts += 1;
                job.Status = "Started";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                logger.LogWarning("ProcessingJob duplicate detected for dedupeKey={DedupeKey}. Reloading existing job.", dedupeKey);

                job = await db.ProcessingJobs
                    .SingleAsync(x => x.TenantId == tenantId && x.JobType == "statement.extract" && x.DedupeKey == dedupeKey, ct);

                job.Attempts += 1;
                job.Status = "Started";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.LastError = null;

                await db.SaveChangesAsync(ct);
            }

            try
            {
                var statement = await db.Statements
                    .SingleAsync(x => x.TenantId == tenantId && x.Id == payload.StatementId, ct);

                var fileObj = await db.FileObjects
                    .SingleAsync(x => x.TenantId == tenantId && x.Id == payload.FileObjectId, ct);

                statement.Status = "Processing";
                await db.SaveChangesAsync(ct);

                var extractor = SelectExtractor(fileObj.ContentType);

                await using var remote = await blobStorage.OpenReadAsync(fileObj.BlobPath, ct);
                await using var ms = new MemoryStream();
                await remote.CopyToAsync(ms, ct);
                ms.Position = 0;

                var sw = Stopwatch.StartNew();
                var normalizedLines = await extractor.ExtractAsync(ms, ct);
                sw.Stop();

                var existingLines = await db.StatementLines
                    .Where(x => x.TenantId == tenantId && x.StatementId == statement.Id)
                    .ToListAsync(ct);

                if (existingLines.Count > 0)
                {
                    db.StatementLines.RemoveRange(existingLines);
                }

                var statementLines = normalizedLines
                    .Select(line => new StatementLine
                    {
                        TenantId = tenantId,
                        StatementId = statement.Id,
                        LineDate = line.LineDate == DateOnly.MinValue ? statement.PeriodStart : line.LineDate,
                        LineType = line.LineType,
                        Description = line.Description,
                        Currency = line.Currency,
                        Amount = line.Amount,
                        TaxAmount = line.TaxAmount
                    })
                    .ToList();

                if (statementLines.Count > 0)
                {
                    db.StatementLines.AddRange(statementLines);
                }

                statement.Status = "Parsed";

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                var incomeTotal = statementLines
                    .Where(x => x.LineType == "Income")
                    .Sum(x => x.Amount);

                var feeTotal = statementLines
                    .Where(x => x.LineType == "Fee")
                    .Sum(x => x.Amount);

                var taxTotal = statementLines
                    .Sum(x => x.TaxAmount ?? (x.LineType == "Tax" ? x.Amount : 0m));

                logger.LogInformation(
                    "Statement parsed in {ElapsedMs}ms. Lines={LineCount} IncomeTotal={IncomeTotal} FeeTotal={FeeTotal} TaxTotal={TaxTotal}",
                    sw.ElapsedMilliseconds,
                    statementLines.Count,
                    incomeTotal,
                    feeTotal,
                    taxTotal);

                var parsedPayload = new StatementParsed(
                    StatementId: statement.Id,
                    TenantId: tenantId,
                    Provider: statement.Provider,
                    PeriodType: statement.PeriodType,
                    PeriodKey: statement.PeriodKey,
                    LineCount: statementLines.Count,
                    IncomeTotal: incomeTotal,
                    FeeTotal: feeTotal,
                    TaxTotal: taxTotal);

                var parsedEnvelope = new MessageEnvelope<StatementParsed>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "statement.parsed.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: parsedPayload);

                await publisher.PublishAsync("q.statement.parsed", parsedEnvelope, ct);
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        private IStatementExtractor SelectExtractor(string contentType)
        {
            var extractor = extractors.FirstOrDefault(x => x.CanHandleContentType(contentType));
            if (extractor is null)
            {
                throw new InvalidOperationException($"No statement extractor registered for content type '{contentType}'.");
            }

            return extractor;
        }
    }

}
