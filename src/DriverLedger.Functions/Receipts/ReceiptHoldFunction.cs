

using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DriverLedger.Functions.Receipts
{
    public sealed class ReceiptHoldFunction(
    DriverLedgerDbContext db,
    ITenantProvider tenantProvider,
    ILogger<ReceiptHoldFunction> log)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        [Function(nameof(ReceiptHoldFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.receipt.hold", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            FunctionContext context,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptHoldV1>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                log.LogError("Invalid HOLD message payload.");
                return;
            }

            var tenantId = envelope.TenantId;
            tenantProvider.SetTenant(tenantId);

            using var scope = log.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = envelope.CorrelationId,
                ["messageId"] = envelope.MessageId,
                ["receiptId"] = envelope.Data.ReceiptId
            });

            var dedupeKey = $"receipt.hold.workflow:{envelope.Data.ReceiptId}";
            var existing = await db.ProcessingJobs.SingleOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.JobType == "receipt.hold.workflow" &&
                x.DedupeKey == dedupeKey, ct);

            if (existing is not null && existing.Status == "Succeeded")
            {
                log.LogInformation("HOLD workflow already processed.");
                return;
            }

            if (existing is null)
            {
                db.ProcessingJobs.Add(new()
                {
                    TenantId = tenantId,
                    JobType = "receipt.hold.workflow",
                    DedupeKey = dedupeKey,
                    Status = "Started",
                    Attempts = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.Attempts += 1;
                existing.Status = "Started";
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.LastError = null;
            }

            try
            {
                // Optional: Ensure notification exists (if you want HOLD queue to be the sole “notifier”)
                // If you keep notifications in ReceiptExtractionHandler already, this can just log.

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "receipt.hold.event.consumed",
                    EntityType = "Receipt",
                    EntityId = envelope.Data.ReceiptId.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = envelope.CorrelationId,
                    MetadataJson = JsonSerializer.Serialize(new { envelope.Data.HoldReason, envelope.Data.Confidence }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);

                var job = await db.ProcessingJobs.SingleAsync(x =>
                    x.TenantId == tenantId && x.JobType == "receipt.hold.workflow" && x.DedupeKey == dedupeKey, ct);

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                log.LogInformation("HOLD workflow completed.");
            }
            catch (Exception ex)
            {
                var job = await db.ProcessingJobs.SingleAsync(x =>
                    x.TenantId == tenantId && x.JobType == "receipt.hold.workflow" && x.DedupeKey == dedupeKey, ct);

                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
                throw;
            }
        }
    }
}
