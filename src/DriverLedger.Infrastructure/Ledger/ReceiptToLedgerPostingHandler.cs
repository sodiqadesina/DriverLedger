using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Notifications;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Ledger
{
    public sealed class ReceiptToLedgerPostingHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IMessagePublisher publisher,
        ILogger<ReceiptToLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        // Temporary “Uncategorized” category id for M1.
        // Later: we replace with real Category table + seeded values.
        private static readonly Guid UncategorizedCategoryId =
            Guid.Parse("00000000-0000-0000-0000-000000000001");

        public async Task HandleAsync(MessageEnvelope<ReceiptExtractedV1> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var payload = envelope.Data;

            // Ensure EF global tenant filters see the tenant
            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["receiptId"] = payload.ReceiptId
            });

            // Skip posting if extraction ended in HOLD
            if (payload.IsHold)
            {
                logger.LogInformation("Receipt is HOLD. Skipping ledger posting. Reason={Reason}", payload.HoldReason);

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.post.skipped",
                    EntityType = "Receipt",
                    EntityId = payload.ReceiptId.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { reason = "HOLD", payload.HoldReason }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);
                return;
            }

            // Idempotency guard #1: ProcessingJobs
            var dedupeKey = $"ledger.post:receipt:{payload.ReceiptId:D}";
            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "ledger.post.receipt" && x.DedupeKey == dedupeKey, ct);

            if (existingJob is not null && existingJob.Status == "Succeeded")
            {
                logger.LogInformation("Ledger posting already succeeded (job dedupe). DedupeKey={DedupeKey}", dedupeKey);
                return;
            }

            // Idempotency guard #2: Unique LedgerEntry (TenantId, SourceType, SourceId)
            var sourceType = "Receipt";
            var sourceId = payload.ReceiptId.ToString("D");

            var alreadyPostedEntry = await db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.SourceType == sourceType && x.SourceId == sourceId, ct);

            if (alreadyPostedEntry is not null)
            {
                logger.LogInformation("Ledger entry already exists (unique guard). EntryId={EntryId}", alreadyPostedEntry.Id);

                // Mark job as succeeded so subsequent retries stop
                if (existingJob is null)
                {
                    db.ProcessingJobs.Add(new Domain.Ops.ProcessingJob
                    {
                        TenantId = tenantId,
                        JobType = "ledger.post.receipt",
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

            // Create or update job attempt
            var job = existingJob;
            if (job is null)
            {
                job = new Domain.Ops.ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "ledger.post.receipt",
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
                // Load receipt + latest extraction (needed for date/amount)
                var receipt = await db.Receipts.SingleAsync(x => x.TenantId == tenantId && x.Id == payload.ReceiptId, ct);

                var extraction = await db.ReceiptExtractions
                    .Where(x => x.TenantId == tenantId && x.ReceiptId == payload.ReceiptId)
                    .OrderByDescending(x => x.ExtractedAt)
                    .FirstAsync(ct);

                var norm = JsonSerializer.Deserialize<NormalizedFields>(extraction.NormalizedFieldsJson, JsonOpts)
                           ?? new NormalizedFields(null, null, null, null, "CAD");

                // Choose entry date
                var entryDate = norm.Date ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

                // Choose amounts (Total includes tax)
                var total = norm.Total ?? 0m;
                var tax = norm.Tax ?? 0m;

                if (total <= 0)
                {
                    // Safety: if we got here with no amount, mark failed and notify/ audit.
                    throw new InvalidOperationException("Cannot post ledger entry: extracted total is missing or <= 0.");
                }

                // Net expense (exclude tax). If tax looks wrong, fail safe to total.
                var net = total - tax;
                if (net < 0) net = total;

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

                // 1) EXPENSE line: NET expense (no GST/HST stored here)
                var expenseLine = new LedgerLine
                {
                    TenantId = tenantId,
                    LedgerEntryId = entry.Id, // IMPORTANT: entry.Id is already generated by Entity
                    CategoryId = UncategorizedCategoryId,
                    Amount = net,
                    GstHst = 0m,
                    LineType = "Expense",
                    DeductiblePct = 1.0m,
                    Memo = norm.Vendor ?? "Receipt expense",
                    AccountCode = null,
                    SourceLinks = new List<LedgerSourceLink>()
                };

                expenseLine.SourceLinks.Add(new LedgerSourceLink
                {
                    TenantId = tenantId,
                    LedgerLineId = expenseLine.Id, // generated by Entity
                    ReceiptId = payload.ReceiptId,
                    FileObjectId = payload.FileObjectId,
                    StatementLineId = null
                });

                entry.Lines.Add(expenseLine);

                // 2) ITC line: TAX only (so snapshots can compute ITC from LineType == "Itc")
                if (tax > 0)
                {
                    var itcLine = new LedgerLine
                    {
                        TenantId = tenantId,
                        LedgerEntryId = entry.Id,
                        CategoryId = UncategorizedCategoryId,
                        Amount = 0m,
                        GstHst = tax,
                        LineType = "Itc",
                        DeductiblePct = 1.0m,
                        Memo = "GST/HST ITC (receipt)",
                        AccountCode = null,
                        SourceLinks = new List<LedgerSourceLink>()
                    };

                    itcLine.SourceLinks.Add(new LedgerSourceLink
                    {
                        TenantId = tenantId,
                        LedgerLineId = itcLine.Id,
                        ReceiptId = payload.ReceiptId,
                        FileObjectId = payload.FileObjectId,
                        StatementLineId = null
                    });

                    entry.Lines.Add(itcLine);
                }

                db.LedgerEntries.Add(entry);

                // Update receipt status to Posted (or ReadyForPosting -> Posted)
                receipt.Status = "Posted";

                db.Notifications.Add(new Notification
                {
                    TenantId = tenantId,
                    Type = "ReceiptPosted",
                    Severity = "Info",
                    Title = "Receipt posted to ledger",
                    Body = $"Posted expense {net:0.00} and ITC {tax:0.00} from receipt.",
                    DataJson = JsonSerializer.Serialize(new { receiptId = receipt.Id, entry.Id }, JsonOpts),
                    Status = "New",
                    CreatedAt = DateTimeOffset.UtcNow
                });

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
                        receiptId = payload.ReceiptId,
                        fileObjectId = payload.FileObjectId,
                        total,
                        net,
                        tax,
                        entryDate = entryDate.ToString("yyyy-MM-dd")
                    }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // Emit ledger.posted.v1 -> q.ledger.posted
                var postedPayload = new LedgerPosted(
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
                    Data: postedPayload
                );

                await publisher.PublishAsync("q.ledger.posted", outEnvelope, ct);
            }
            catch (DbUpdateException dbEx)
            {
                // If unique constraint fired due to race, treat as already posted.
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
                    EntityType = "Receipt",
                    EntityId = payload.ReceiptId.ToString("D"),
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
                    DataJson = JsonSerializer.Serialize(new { receiptId = payload.ReceiptId }, JsonOpts),
                    Status = "New",
                    CreatedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        private sealed record NormalizedFields(
            DateOnly? Date,
            string? Vendor,
            decimal? Total,
            decimal? Tax,
            string Currency
        );
    }
}
