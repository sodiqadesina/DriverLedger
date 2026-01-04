using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Notifications;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Ledger
{
    public sealed class StatementToLedgerPostingHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IMessagePublisher publisher,
        ILogger<StatementToLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private static readonly Guid UncategorizedCategoryId =
            Guid.Parse("00000000-0000-0000-0000-000000000001");

        public async Task HandleAsync(MessageEnvelope<StatementParsed> envelope, CancellationToken ct)
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

            var dedupeKey = $"ledger.post:statement:{payload.StatementId:D}";
            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "ledger.post.statement" && x.DedupeKey == dedupeKey, ct);

            if (existingJob is not null && existingJob.Status == "Succeeded")
            {
                logger.LogInformation("Ledger posting already succeeded (job dedupe). DedupeKey={DedupeKey}", dedupeKey);
                return;
            }

            var sourceType = "Statement";
            var sourceId = payload.StatementId.ToString("D");

            var alreadyPostedEntry = await db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SourceType == sourceType && x.SourceId == sourceId, ct);

            if (alreadyPostedEntry is not null)
            {
                logger.LogInformation("Ledger entry already exists (unique guard). EntryId={EntryId}", alreadyPostedEntry.Id);

                if (existingJob is null)
                {
                    db.ProcessingJobs.Add(new Domain.Ops.ProcessingJob
                    {
                        TenantId = tenantId,
                        JobType = "ledger.post.statement",
                        DedupeKey = dedupeKey,
                        Status = "Succeeded",
                        Attempts = 1,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    existingJob.Status = "Succeeded";
                    existingJob.UpdatedAt = DateTimeOffset.UtcNow;
                    existingJob.LastError = null;
                }

                await db.SaveChangesAsync(ct);
                return;
            }

            var job = existingJob;
            if (job is null)
            {
                job = new Domain.Ops.ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "ledger.post.statement",
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
                job.Attempts += 1;
                job.Status = "Started";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
            }

            try
            {
                var statement = await db.Statements
                    .SingleAsync(x => x.TenantId == tenantId && x.Id == payload.StatementId, ct);

                var lines = await db.StatementLines
                    .Where(x => x.TenantId == tenantId && x.StatementId == statement.Id)
                    .ToListAsync(ct);

                var entryDate = statement.PeriodEnd;

                var entry = new LedgerEntry
                {
                    TenantId = tenantId,
                    EntryDate = entryDate,
                    SourceType = sourceType,
                    SourceId = sourceId,
                    PostedByUserId = null,
                    PostedByType = "System",
                    CorrelationId = correlationId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Lines = new List<LedgerLine>()
                };

                var postedCount = 0;
                var metricSkippedCount = 0;

                foreach (var line in lines)
                {
                    // ===== IMPORTANT RULE =====
                    // Do NOT post Metric lines into ledger money accounts.
                    // Metrics should remain non-posting informational (or later go into a metrics store).
                    if (line.IsMetric || string.Equals(line.LineType, "Metric", StringComparison.OrdinalIgnoreCase))
                    {
                        metricSkippedCount++;
                        continue;
                    }

                    // Monetary amount: prefer MoneyAmount; fall back to legacy Amount.
                    // If both are missing, treat as 0 (should be rare; better than throwing).
                    var money = line.MoneyAmount ?? 0m;

                    var ledgerLine = new LedgerLine
                    {
                        TenantId = tenantId,
                        LedgerEntryId = entry.Id,
                        CategoryId = UncategorizedCategoryId,
                        LineType = string.IsNullOrWhiteSpace(line.LineType) ? "Fee" : line.LineType,
                        Amount = money,
                        GstHst = line.TaxAmount ?? 0m,
                        DeductiblePct = 1.0m,
                        Memo = line.Description,
                        AccountCode = null,
                        SourceLinks = new List<LedgerSourceLink>()
                    };

                    ledgerLine.SourceLinks.Add(new LedgerSourceLink
                    {
                        TenantId = tenantId,
                        LedgerLineId = ledgerLine.Id,
                        ReceiptId = null,
                        StatementLineId = line.Id,
                        FileObjectId = statement.FileObjectId
                    });

                    entry.Lines.Add(ledgerLine);
                    postedCount++;
                }

                db.LedgerEntries.Add(entry);

                statement.Status = "Posted";

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.posted",
                    EntityType = "LedgerEntry",
                    EntityId = entry.Id.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        sourceType,
                        sourceId,
                        statementId = statement.Id,
                        entryDate = entryDate.ToString("yyyy-MM-dd"),
                        totalLines = lines.Count,
                        postedMonetaryLines = postedCount,
                        skippedMetricLines = metricSkippedCount
                    }, JsonOpts)
                });

                db.Notifications.Add(new Notification
                {
                    TenantId = tenantId,
                    Type = "StatementPosted",
                    Severity = "Info",
                    Title = "Statement posted to ledger",
                    Body = $"Posted {postedCount} monetary lines. Skipped {metricSkippedCount} metric lines.",
                    DataJson = JsonSerializer.Serialize(new { statementId = statement.Id, entry.Id }, JsonOpts),
                    Status = "New",
                    CreatedAt = DateTimeOffset.UtcNow
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                var postedPayload = new Application.Receipts.Messages.LedgerPosted(
                    LedgerEntryId: entry.Id,
                    SourceType: sourceType,
                    SourceId: sourceId,
                    EntryDate: entryDate.ToString("yyyy-MM-dd")
                );

                var outEnvelope = new MessageEnvelope<Application.Receipts.Messages.LedgerPosted>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "ledger.posted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: postedPayload
                );

                await publisher.PublishAsync("q.ledger.posted", outEnvelope, ct);
            }
            catch (DbUpdateException dbEx)
            {
                logger.LogWarning(dbEx, "DbUpdateException during posting. Assuming duplicate posting prevented by unique constraint.");

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.LastError = null;

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ledger posting failed.");

                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.post.failed",
                    EntityType = "Statement",
                    EntityId = payload.StatementId.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts)
                });

                db.Notifications.Add(new Notification
                {
                    TenantId = tenantId,
                    Type = "LedgerPostFailed",
                    Severity = "Error",
                    Title = "Ledger posting failed",
                    Body = ex.Message,
                    DataJson = JsonSerializer.Serialize(new { statementId = payload.StatementId }, JsonOpts),
                    Status = "New",
                    CreatedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }
    }
}
