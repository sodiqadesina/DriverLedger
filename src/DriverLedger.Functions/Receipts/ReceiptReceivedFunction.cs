using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Domain.Ops;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DriverLedger.Functions.Receipts
{
    public sealed class ReceiptReceivedFunction
    {
        private readonly DriverLedgerDbContext _db;
        private readonly ITenantProvider _tenant;
        private readonly ILogger<ReceiptReceivedFunction> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public ReceiptReceivedFunction(DriverLedgerDbContext db, ITenantProvider tenant, ILogger<ReceiptReceivedFunction> log)
        {
            _db = db;
            _tenant = tenant;
            _log = log;
        }

        [Function(nameof(ReceiptReceivedFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.receipt.received", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            FunctionContext context,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptReceived>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                _log.LogError("Invalid message payload (cannot deserialize).");
                return;
            }

            // Set tenant scope for EF global query filters
            _tenant.SetTenant(envelope.TenantId);

            using (_log.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = envelope.CorrelationId,
                ["TenantId"] = envelope.TenantId,
                ["MessageType"] = envelope.Type,
                ["MessageId"] = envelope.MessageId
            }))
            {
                // ---- Idempotency gate ----
                // One job per (TenantId, JobType, DedupeKey).
                // If message is delivered twice, second attempt no-ops.
                var dedupeKey = $"receipt:{envelope.Data.ReceiptId}";
                var jobType = "ReceiptReceived";

                var existing = _db.ProcessingJobs
                    .FirstOrDefault(j => j.JobType == jobType && j.DedupeKey == dedupeKey);

                if (existing is not null && existing.Status == "Succeeded")
                {
                    _log.LogInformation("Duplicate message ignored (already succeeded).");
                    return;
                }

                var job = existing ?? new ProcessingJob
                {
                    TenantId = envelope.TenantId,
                    JobType = jobType,
                    DedupeKey = dedupeKey,
                    Status = "Started",
                    Attempts = 0
                };

                job.Attempts += 1;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                if (existing is null) _db.ProcessingJobs.Add(job);
                await _db.SaveChangesAsync(ct);

                try
                {
                    // Skeleton: This is where you’ll call Document Intelligence later.
                    // For foundation sprint: just log and mark succeeded.
                    _log.LogInformation("ReceiptReceived skeleton executing. ReceiptId={ReceiptId}", envelope.Data.ReceiptId);

                    job.Status = "Succeeded";
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);

                    _log.LogInformation("ReceiptReceived skeleton completed.");
                }
                catch (Exception ex)
                {
                    job.Status = "Failed";
                    job.LastError = ex.Message;
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);

                    _log.LogError(ex, "ReceiptReceived failed.");
                    throw; // Let SB retry + DLQ per policy
                }
            }
        }

        public sealed record ReceiptReceived(Guid ReceiptId, Guid FileObjectId);
    }
}
