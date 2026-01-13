using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Ops;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Ledger
{
    /// <summary>
    /// Posts extracted StatementLines into the Ledger as a single LedgerEntry.
    ///
    /// Posting rules:
    /// - "Most granular wins" is enforced here (not only at upload/submit time),
    ///   because parsing may reset Status to Draft.
    ///   Example: if Monthly exists for the year, then Yearly/Quarterly/YTD must NOT post.
    /// - "ReconciliationOnly" statements MUST NEVER post to the ledger.
    /// - "Draft" or "Submitted" statements ARE allowed to post (draft is expected due to parsing workflow).
    /// - "Posted" is idempotent (if already posted, no-op).
    ///
    /// Notes:
    /// - Metric lines are never posted to the ledger.
    /// - We keep a dedupe ProcessingJob + a unique SourceType/SourceId guard.
    /// </summary>
    public sealed class StatementToLedgerPostingHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IMessagePublisher publisher,
        ILogger<StatementToLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        // TODO: Replace with real Uncategorized category ID (or load from config/table).
        private static readonly Guid UncategorizedCategoryId =
            Guid.Parse("00000000-0000-0000-0000-000000000001");

        public async Task HandleAsync(MessageEnvelope<StatementParsed> envelope, CancellationToken ct)
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
                ["statementId"] = msg.StatementId
            });

            // -------------------------------------------------------
            // Load statement
            // -------------------------------------------------------
            var statement = await db.Statements
                .SingleAsync(s => s.TenantId == tenantId && s.Id == msg.StatementId, ct);

            // -------------------------------------------------------
            // Enforce "most granular wins" at posting time.
            //
            // This is REQUIRED because extraction can set Status="Draft".
            // We block posting if a more granular statement exists for the same year+provider.
            // -------------------------------------------------------
            var year = statement.PeriodStart.Year;
            var incomingRank = GetGranularityRank(statement.PeriodType);

            var otherTypesThisYear = await db.Statements
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId
                         && s.Provider == statement.Provider
                         && s.Id != statement.Id
                         && s.PeriodStart.Year == year)
                .Select(s => s.PeriodType)
                .ToListAsync(ct);

            var mostGranularExistingRank = otherTypesThisYear.Count == 0
                ? 0
                : otherTypesThisYear.Max(GetGranularityRank);

            if (incomingRank < mostGranularExistingRank)
            {
                logger.LogInformation(
                    "Skipping ledger posting for StatementId={StatementId} because a more granular statement exists for Provider={Provider}, Year={Year}. IncomingPeriodType={PeriodType}.",
                    statement.Id, statement.Provider, year, statement.PeriodType);

                // Make the state explicit and stable for future replays.
                if (!string.Equals(statement.Status, "ReconciliationOnly", StringComparison.OrdinalIgnoreCase))
                {
                    statement.Status = "ReconciliationOnly";
                    await db.SaveChangesAsync(ct);
                }

                return;
            }

            // -------------------------------------------------------
            // Enforce status rules
            // -------------------------------------------------------

            // Hard stop: reconciliation-only statements should never hit the ledger.
            if (string.Equals(statement.Status, "ReconciliationOnly", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Skipping ledger posting for StatementId={StatementId} because Status={Status}. (Extraction OK, posting blocked.)",
                    statement.Id, statement.Status);

                return;
            }

            // Allow Draft/Submitted to post (Draft is expected due to parsing workflow).
            var postable =
                string.Equals(statement.Status, "Draft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(statement.Status, "Submitted", StringComparison.OrdinalIgnoreCase);

            if (!postable)
            {
                logger.LogInformation(
                    "Skipping ledger posting for StatementId={StatementId} because Status={Status} is not postable.",
                    statement.Id, statement.Status);

                return;
            }

            // -------------------------------------------------------
            // Idempotency gates
            // -------------------------------------------------------
            var dedupeKey = $"ledger.post:statement:{statement.Id:D}";

            var job = await db.ProcessingJobs
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "ledger.post.statement" &&
                    x.DedupeKey == dedupeKey, ct);

            if (job is not null && string.Equals(job.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Ledger posting already succeeded (job dedupe). DedupeKey={DedupeKey}", dedupeKey);
                return;
            }

            // Unique guard on ledger source (strongest protection)
            const string sourceType = "Statement";
            var sourceId = statement.Id.ToString("D");

            var alreadyPostedEntry = await db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(e =>
                    e.TenantId == tenantId &&
                    e.SourceType == sourceType &&
                    e.SourceId == sourceId, ct);

            if (alreadyPostedEntry is not null)
            {
                logger.LogInformation(
                    "Ledger entry already exists (unique source guard). EntryId={EntryId}",
                    alreadyPostedEntry.Id);

                // Also mark the job succeeded so replays quiet down.
                if (job is null)
                {
                    db.ProcessingJobs.Add(new ProcessingJob
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
                    job.Status = "Succeeded";
                    job.LastError = null;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                }

                // Ensure statement reflects reality
                statement.Status = "Posted";

                await db.SaveChangesAsync(ct);
                return;
            }

            // Create/refresh job
            if (job is null)
            {
                job = new ProcessingJob
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
                job.LastError = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }

            try
            {
                // -------------------------------------------------------
                // Load statement lines and build ledger entry
                // -------------------------------------------------------
                var statementLines = await db.StatementLines
                    .Where(x => x.TenantId == tenantId && x.StatementId == statement.Id)
                    .ToListAsync(ct);

                // Use PeriodEnd as the ledger EntryDate for the statement posting.
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

                var postedMonetary = 0;
                var skippedMetric = 0;

                foreach (var sl in statementLines)
                {
                    // Never post metrics to ledger
                    if (sl.IsMetric || string.Equals(sl.LineType, "Metric", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedMetric++;
                        continue;
                    }

                    var ledgerLine = new LedgerLine
                    {
                        TenantId = tenantId,
                        LedgerEntryId = entry.Id,
                        CategoryId = UncategorizedCategoryId,
                        LineType = sl.LineType,
                        Amount = sl.MoneyAmount ?? 0m,
                        GstHst = sl.TaxAmount ?? 0m,
                        DeductiblePct = 1.0m,
                        Memo = sl.Description,
                        SourceLinks = new List<LedgerSourceLink>
                        {
                            new LedgerSourceLink
                            {
                                TenantId = tenantId,
                                LedgerLineId = Guid.NewGuid(),
                                StatementLineId = sl.Id,
                                FileObjectId = statement.FileObjectId
                            }
                        }
                    };

                    entry.Lines.Add(ledgerLine);
                    postedMonetary++;
                }

                db.LedgerEntries.Add(entry);

                // Mark statement as posted only after we have built the ledger entry.
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
                        statementId = statement.Id.ToString("D"),
                        provider = statement.Provider,
                        periodType = statement.PeriodType,
                        periodKey = statement.PeriodKey,
                        entryDate = entryDate.ToString("yyyy-MM-dd"),
                        totalStatementLines = statementLines.Count,
                        postedMonetaryLines = postedMonetary,
                        skippedMetricLines = skippedMetric
                    }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // Publish ledger.posted so snapshots recompute
                var posted = new LedgerPosted(
                    LedgerEntryId: entry.Id,
                    SourceType: sourceType,
                    SourceId: sourceId,
                    EntryDate: entryDate.ToString("yyyy-MM-dd")
                );

                var outEnvelope = new MessageEnvelope<LedgerPosted>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "ledger.posted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: posted
                );

                await publisher.PublishAsync("q.ledger.posted", outEnvelope, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ledger posting failed for StatementId={StatementId}", statement.Id);

                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        /// <summary>
        /// Granularity: Monthly (3) > Quarterly (2) > YTD/Yearly (1)
        /// </summary>
        private static int GetGranularityRank(string? periodType)
        {
            if (string.IsNullOrWhiteSpace(periodType)) return 0;

            return periodType.Trim().ToLowerInvariant() switch
            {
                "monthly" => 3,
                "quarterly" => 2,
                "ytd" => 1,
                "yearly" => 1,
                _ => 0
            };
        }
    }
}
