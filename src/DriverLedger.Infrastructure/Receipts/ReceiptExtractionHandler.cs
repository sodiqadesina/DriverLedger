
using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Notifications;
using DriverLedger.Domain.Ops;
using DriverLedger.Domain.Receipts.Extraction;
using DriverLedger.Domain.Receipts.Review;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Receipts
{
    public sealed class ReceiptExtractionHandler(
    DriverLedgerDbContext db,
    ITenantProvider tenantProvider,
    IReceiptExtractor extractor,
    IBlobStorage blobStorage,
    IMessagePublisher publisher,
    ILogger<ReceiptExtractionHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task HandleAsync(MessageEnvelope<ReceiptReceived> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var payload = envelope.Data;

            // Ensure EF global tenant filters see the current tenant.
            
            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["receiptId"] = payload.ReceiptId
            });

            var dedupeKey = $"receipt.extract:{payload.ReceiptId}";

            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == "receipt.extract" && x.DedupeKey == dedupeKey, ct);

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
                    JobType = "receipt.extract",
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
                var receipt = await db.Receipts.SingleAsync(x => x.TenantId == tenantId && x.Id == payload.ReceiptId, ct);
                var fileObj = await db.FileObjects.SingleAsync(x => x.TenantId == tenantId && x.Id == payload.FileObjectId, ct);

                // Adjust the property name to match your FileObject model:
                var blobPath = fileObj.BlobPath;

                await using var stream = await blobStorage.OpenReadAsync(blobPath, ct);
                var normalized = await extractor.ExtractAsync(stream, ct);

                var confidence = ReceiptConfidenceCalculator.Compute(normalized);

                var decision = ReceiptHoldEvaluator.Evaluate(normalized, confidence);

                var isHold = decision.IsHold;
                var holdReason = decision.Reason;
                var questionsJson = decision.QuestionsJson;


                db.ReceiptExtractions.Add(new ReceiptExtraction
                {
                    TenantId = tenantId,
                    ReceiptId = receipt.Id,
                    ModelVersion = extractor.ModelVersion,
                    RawJson = normalized.RawJson,
                    NormalizedFieldsJson = JsonSerializer.Serialize(new
                    {
                        normalized.Date,
                        normalized.Vendor,
                        normalized.Total,
                        normalized.Tax,
                        normalized.Currency
                    }, JsonOpts),
                    Confidence = confidence,
                    ExtractedAt = DateTimeOffset.UtcNow
                });

                if (isHold)
                {
                    receipt.Status = "HOLD";

                    db.ReceiptReviews.Add(new ReceiptReview
                    {
                        TenantId = tenantId,
                        ReceiptId = receipt.Id,
                        HoldReason = holdReason,
                        QuestionsJson = questionsJson
                    });

                    db.Notifications.Add(new Notification
                    {
                        TenantId = tenantId,
                        Type = "ReceiptHold",
                        Severity = "Warning",
                        Title = "Receipt needs review",
                        Body = holdReason,
                        DataJson = JsonSerializer.Serialize(new { receiptId = receipt.Id }, JsonOpts),
                        Status = "New"
                    });

                    db.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        ActorUserId = "system",
                        Action = "receipt.hold",
                        EntityType = "Receipt",
                        EntityId = receipt.Id.ToString("D"),
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = correlationId,
                        MetadataJson = JsonSerializer.Serialize(new { holdReason, confidence }, JsonOpts)
                    });
                }
                else
                {
                    receipt.Status = "ReadyForPosting";

                    db.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        ActorUserId = "system",
                        Action = "receipt.extracted",
                        EntityType = "Receipt",
                        EntityId = receipt.Id.ToString("D"),
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = correlationId,
                        MetadataJson = JsonSerializer.Serialize(new { confidence }, JsonOpts)
                    });
                }

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // Emit receipt.extracted.v1
                var extractedPayload = new ReceiptExtractedV1(
                    ReceiptId: receipt.Id,
                    FileObjectId: receipt.FileObjectId,
                    Confidence: confidence,
                    IsHold: isHold,
                    HoldReason: holdReason
                );

                var outEnvelope = new MessageEnvelope<ReceiptExtractedV1>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "receipt.extracted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: extractedPayload
                );

                await publisher.PublishAsync("q.receipt.extracted", outEnvelope, ct);
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
                    Action = "receipt.extract.failed",
                    EntityType = "Receipt",
                    EntityId = payload.ReceiptId.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

       
    }
}
