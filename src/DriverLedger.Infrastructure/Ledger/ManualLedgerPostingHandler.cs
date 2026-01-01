

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
    public sealed class ManualLedgerPostingHandler(
       DriverLedgerDbContext db,
       ITenantProvider tenantProvider,
       IMessagePublisher publisher,
       ILogger<ManualLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task<object> CreateManualAsync(
            ManualLedgerEntryRequest req,
            string correlationId,
            CancellationToken ct)
        {
            var tenantId = tenantProvider.TenantId ?? throw new InvalidOperationException("Tenant is not set.");

            if (req.Lines is null || req.Lines.Count == 0)
                throw new InvalidOperationException("Manual entry must include at least one line.");

            if (req.Lines.Any(l => l.Amount == 0))
                throw new InvalidOperationException("Manual entry lines cannot have amount = 0.");

            var idempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey)
                ? $"ledger.manual:{Guid.NewGuid():N}"
                : $"ledger.manual:{req.IdempotencyKey.Trim()}";

            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "ledger.post.manual" &&
                    x.DedupeKey == idempotencyKey, ct);

            if (existingJob is not null && existingJob.Status == "Succeeded")
            {
                logger.LogInformation("Manual posting already succeeded. DedupeKey={DedupeKey}", idempotencyKey);
                return new { status = "AlreadySucceeded", dedupeKey = idempotencyKey };
            }

            ProcessingJob job;
            if (existingJob is null)
            {
                job = new ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "ledger.post.manual",
                    DedupeKey = idempotencyKey,
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
                var entry = new LedgerEntry
                {
                    TenantId = tenantId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    EntryDate = req.EntryDate,

                    SourceType = "Manual",
                    // placeholder; we’ll set to entry.Id after SaveChanges
                    SourceId = "pending",

                    PostedByType = "Driver",
                    PostedByUserId = null, // wire real auth later

                    CorrelationId = correlationId
                };

                db.LedgerEntries.Add(entry);

                // Save FIRST so entry.Id is real (Entity base controls Id)
                await db.SaveChangesAsync(ct);

                // Now that Id exists, set SourceId to the stable entry id
                entry.SourceId = entry.Id.ToString("D");

                foreach (var l in req.Lines)
                {
                    db.LedgerLines.Add(new LedgerLine
                    {
                        LedgerEntryId = entry.Id,
                        CategoryId = l.CategoryId,

                        AccountCode = l.AccountCode,
                        Amount = l.Amount,
                        GstHst = l.GstHst ?? 0m,
                        DeductiblePct = l.DeductiblePct ?? 1.0m,
                        Memo = l.Memo ?? req.Memo
                    });
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.manual.posted",
                    EntityType = "LedgerEntry",
                    EntityId = entry.Id.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { dedupeKey = idempotencyKey }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                var posted = new MessageEnvelope<LedgerPostedV1>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "ledger.posted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: new LedgerPostedV1(entry.Id, "Manual", entry.SourceId, entry.EntryDate)
                );

                await publisher.PublishAsync("q.ledger.posted", posted, ct);

                return new { status = "Succeeded", ledgerEntryId = entry.Id, dedupeKey = idempotencyKey };
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
                    Action = "ledger.manual.failed",
                    EntityType = "LedgerEntry",
                    EntityId = "unknown",
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { error = ex.Message, dedupeKey = idempotencyKey }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }
    }
}
