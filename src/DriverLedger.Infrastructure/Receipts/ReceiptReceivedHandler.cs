using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ops;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Receipts;

public sealed class ReceiptReceivedHandler : IReceiptReceivedHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly DriverLedgerDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IClock _clock;
    private readonly ILogger<ReceiptReceivedHandler> _log;

    public ReceiptReceivedHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IClock clock,
        ILogger<ReceiptReceivedHandler> log)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _clock = clock;
        _log = log;
    }

    public async Task HandleAsync(MessageEnvelope<ReceiptReceived> envelope, CancellationToken ct)
    {
        // Ensure EF global query filters apply the correct tenant (your provider uses AsyncLocal)
        _tenantProvider.SetTenant(envelope.TenantId);

        using var scope = _log.BeginScope(new Dictionary<string, object?>
        {
            ["tenantId"] = envelope.TenantId,
            ["correlationId"] = envelope.CorrelationId,
            ["messageId"] = envelope.MessageId,
            ["messageType"] = envelope.Type
        });

        var receiptId = envelope.Data.ReceiptId;

        // Keep dedupe stable and deterministic
        var dedupeKey = $"receipt.received.v1:{envelope.TenantId:D}:{receiptId:D}";

        // 1) Idempotency gate via ProcessingJobs (unique DedupeKey per tenant)
        var job = new ProcessingJob
        {
            TenantId = envelope.TenantId,
            JobType = envelope.Type,      // e.g. "receipt.received.v1"
            DedupeKey = dedupeKey,
            Status = "Started",
            Attempts = 1,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        _db.ProcessingJobs.Add(job);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _log.LogInformation("Duplicate message detected by dedupe key. Skipping processing.");

            // Optional: audit duplicate
            await WriteSystemAuditAsync(
                tenantId: envelope.TenantId,
                correlationId: envelope.CorrelationId,
                action: "receipt.received.duplicate",
                entityType: "Receipt",
                entityId: receiptId.ToString("D"),
                metadata: new { envelope.MessageId, envelope.Type, dedupeKey },
                ct: ct);

            return;
        }

        try
        {
            // 2) Update receipt state (stub next step)
            var receipt = await _db.Receipts.SingleAsync(r => r.Id == receiptId, ct);

            // Keep consistent with your API setting "Processing"
            if (string.Equals(receipt.Status, "Processing", StringComparison.OrdinalIgnoreCase))
            {
                receipt.Status = "ExtractionPending";
            }

            // 3) Mark job success
            job.Status = "Succeeded";
            job.UpdatedAt = _clock.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 4) Audit success
            await WriteSystemAuditAsync(
                tenantId: envelope.TenantId,
                correlationId: envelope.CorrelationId,
                action: "receipt.received.succeeded",
                entityType: "Receipt",
                entityId: receiptId.ToString("D"),
                metadata: new { envelope.MessageId, envelope.Type, dedupeKey },
                ct: ct);

            _log.LogInformation("ReceiptReceived processed successfully.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ReceiptReceived processing failed.");

            job.Status = "Failed";
            job.LastError = ex.Message;
            job.UpdatedAt = _clock.UtcNow;

            // Optional: set receipt to Failed if it exists
            try
            {
                var receipt = await _db.Receipts.SingleOrDefaultAsync(r => r.Id == receiptId, ct);
                if (receipt is not null)
                {
                    receipt.Status = "Failed";
                }
            }
            catch
            {
                // swallow secondary errors
            }

            await _db.SaveChangesAsync(ct);

            await WriteSystemAuditAsync(
                tenantId: envelope.TenantId,
                correlationId: envelope.CorrelationId,
                action: "receipt.received.failed",
                entityType: "Receipt",
                entityId: receiptId.ToString("D"),
                metadata: new { envelope.MessageId, envelope.Type, dedupeKey, error = ex.Message },
                ct: ct);

            throw; // allow SB retry policy
        }
    }

    private async Task WriteSystemAuditAsync(
        Guid tenantId,
        string correlationId,
        string action,
        string entityType,
        string entityId,
        object? metadata,
        CancellationToken ct)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            // DO NOT set Id (Entity.Id setter is not public)
            TenantId = tenantId,
            ActorUserId = "system",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OccurredAt = _clock.UtcNow,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOpts)
        });

        await _db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // SQL Server unique constraint errors: 2601 (duplicate key), 2627 (unique constraint)
        var sqlEx = ex.InnerException as SqlException
                    ?? ex.GetBaseException() as SqlException;

        return sqlEx is not null && (sqlEx.Number == 2601 || sqlEx.Number == 2627);
    }

    // This keeps the handler compiling even if Receipt doesn't have UpdatedAt in your model.
    // If your Receipt DOES have UpdatedAt, you can delete this method and the conditional.
    private static bool HasUpdatedAt(dynamic receipt)
    {
        try
        {
            var _ = receipt.UpdatedAt;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
