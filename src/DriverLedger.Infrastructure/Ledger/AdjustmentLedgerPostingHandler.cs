

using DriverLedger.Application.Common;
using DriverLedger.Application.Ledger.Commands;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Ops;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Ledger
{
    public sealed class AdjustmentLedgerPostingHandler(
      DriverLedgerDbContext db,
      ITenantProvider tenantProvider,
      IMessagePublisher publisher,
      ILogger<AdjustmentLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task<object> CreateAdjustmentAsync(
            AdjustmentRequest req,
            string correlationId,
            CancellationToken ct)
        {
            var tenantId = tenantProvider.TenantId ?? throw new InvalidOperationException("Tenant is not set.");

            // Load entry to reverse (tenant-scoped)
            var toReverse = await db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.ReverseEntryId, ct);

            if (toReverse is null)
                throw new InvalidOperationException("ReverseEntryId not found.");

            // LedgerLine is NOT tenant-scoped; it's scoped by its parent LedgerEntryId
            var reverseLines = await db.LedgerLines
                .AsNoTracking()
                .Where(x => x.LedgerEntryId == req.ReverseEntryId)
                .ToListAsync(ct);

            if (reverseLines.Count == 0)
                throw new InvalidOperationException("ReverseEntryId has no lines.");

            // Idempotency
            var dedupeKey = $"ledger.adjust:{req.ReverseEntryId:D}:{req.Corrected.IdempotencyKey ?? "no-key"}";

            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "ledger.post.adjustment" &&
                    x.DedupeKey == dedupeKey, ct);

            if (existingJob is not null && existingJob.Status == "Succeeded")
            {
                logger.LogInformation("Adjustment already succeeded. DedupeKey={DedupeKey}", dedupeKey);
                return new { status = "AlreadySucceeded", dedupeKey };
            }

            ProcessingJob job;
            if (existingJob is null)
            {
                job = new ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "ledger.post.adjustment",
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
                // 1) Reversal entry
                // IMPORTANT: put reversal in the SAME period/date as the entry being reversed
                var reversalEntry = new LedgerEntry
                {
                    TenantId = tenantId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    EntryDate = toReverse.EntryDate,

                    SourceType = "Adjustment",
                    SourceId = $"reverse:{req.ReverseEntryId:D}",

                    PostedByType = "System",
                    PostedByUserId = null,

                    CorrelationId = correlationId
                };

                db.LedgerEntries.Add(reversalEntry);

                // 2) Corrected entry
                var correctedEntry = new LedgerEntry
                {
                    TenantId = tenantId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    EntryDate = req.Corrected.EntryDate,

                    SourceType = "Adjustment",
                    SourceId = $"corrected:{req.ReverseEntryId:D}",

                    PostedByType = "Driver",
                    PostedByUserId = null,

                    CorrelationId = correlationId
                };

                db.LedgerEntries.Add(correctedEntry);

                // Save entries FIRST so they have real Ids (your Entity base controls Id)
                await db.SaveChangesAsync(ct);

                // Reversal lines (explicit FK)
                foreach (var l in reverseLines)
                {
                    db.LedgerLines.Add(new LedgerLine
                    {
                        LedgerEntryId = reversalEntry.Id,
                        CategoryId = l.CategoryId,

                        AccountCode = l.AccountCode,
                        Amount = -l.Amount,
                        GstHst = -l.GstHst,
                        DeductiblePct = l.DeductiblePct,
                        Memo = $"Reversal of {req.ReverseEntryId:D}"
                    });
                }

                // Corrected lines (explicit FK)
                foreach (var l in req.Corrected.Lines)
                {
                    db.LedgerLines.Add(new LedgerLine
                    {
                        LedgerEntryId = correctedEntry.Id,
                        CategoryId = l.CategoryId,

                        AccountCode = l.AccountCode,
                        Amount = l.Amount,
                        GstHst = l.GstHst ?? 0m,
                        DeductiblePct = l.DeductiblePct ?? 1.0m,
                        Memo = l.Memo ?? req.Corrected.Memo
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.adjustment.posted",
                    EntityType = "LedgerEntry",
                    EntityId = correctedEntry.Id.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        reverseEntryId = req.ReverseEntryId,
                        reversalEntryId = reversalEntry.Id,
                        correctedEntryId = correctedEntry.Id,
                        dedupeKey
                    }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // Publish for snapshots
                await publisher.PublishAsync(
                    "q.ledger.posted",
                    new MessageEnvelope<LedgerPostedV1>(
                        Guid.NewGuid().ToString("N"),
                        "ledger.posted.v1",
                        DateTimeOffset.UtcNow,
                        tenantId,
                        correlationId,
                        new LedgerPostedV1(
                            reversalEntry.Id,
                            "Adjustment",
                            reversalEntry.SourceId,
                            reversalEntry.EntryDate
                        )
                    ),
                    ct);

                await publisher.PublishAsync(
                    "q.ledger.posted",
                    new MessageEnvelope<LedgerPostedV1>(
                        Guid.NewGuid().ToString("N"),
                        "ledger.posted.v1",
                        DateTimeOffset.UtcNow,
                        tenantId,
                        correlationId,
                        new LedgerPostedV1(
                            correctedEntry.Id,
                            "Adjustment",
                            correctedEntry.SourceId,
                            correctedEntry.EntryDate
                        )
                    ),
                    ct);

                return new
                {
                    status = "Succeeded",
                    dedupeKey,
                    reversalEntryId = reversalEntry.Id,
                    correctedEntryId = correctedEntry.Id
                };
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.adjustment.failed",
                    EntityType = "LedgerEntry",
                    EntityId = req.ReverseEntryId.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { error = ex.Message, dedupeKey }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }
    }
}
