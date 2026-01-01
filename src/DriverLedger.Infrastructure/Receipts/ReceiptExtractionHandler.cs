
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

        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            if (ex.InnerException is not Microsoft.Data.SqlClient.SqlException sql) return false;
            return sql.Number is 2601 or 2627;
        }

        public async Task HandleAsync(MessageEnvelope<ReceiptReceived> envelope, CancellationToken ct)
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
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                logger.LogWarning("ProcessingJob duplicate detected for dedupeKey={DedupeKey}. Reloading existing job.", dedupeKey);

                job = await db.ProcessingJobs
                    .SingleAsync(x => x.TenantId == tenantId && x.JobType == "receipt.extract" && x.DedupeKey == dedupeKey, ct);

                job.Attempts += 1;
                job.Status = "Started";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.LastError = null;

                await db.SaveChangesAsync(ct);
            }

            try
            {
                var receipt = await db.Receipts.SingleAsync(x => x.TenantId == tenantId && x.Id == payload.ReceiptId, ct);
                var fileObj = await db.FileObjects.SingleAsync(x => x.TenantId == tenantId && x.Id == payload.FileObjectId, ct);

                await using var remote = await blobStorage.OpenReadAsync(fileObj.BlobPath, ct);

                await using var ms = new MemoryStream();
                await remote.CopyToAsync(ms, ct);
                ms.Position = 0;

                var normalized = await extractor.ExtractAsync(ms, ct);

                var policyConfidence = ReceiptConfidenceCalculator.Compute(normalized);
                var decision = ReceiptHoldEvaluator.Evaluate(normalized, policyConfidence);

                var isHold = decision.IsHold;
                var holdReason = decision.Reason;
                var questionsJson = decision.QuestionsJson;

                // ----------------------------
                // (1) ALWAYS persist stable CORE normalized fields for posting/snapshots
                // ----------------------------
                var normalizedFieldsJson = JsonSerializer.Serialize(new
                {
                    date = normalized.Date,         // DateOnly? serializes as "YYYY-MM-DD"
                    vendor = normalized.Vendor,
                    total = normalized.Total,
                    tax = normalized.Tax,
                    currency = normalized.Currency
                }, JsonOpts);

                // ----------------------------
                // (2) Stash "extras" (if any) inside RawJson without DB migration
                // RawJson = { raw: <rawProjection>, extras: <extractorExtras|null> }
                // ----------------------------
                var rawElement = SafeParseJsonElementOrString(normalized.RawJson);
                var extrasElement = SafeParseJsonElementOrNull(normalized.NormalizedFieldsJson);

                var wrappedRawJson = JsonSerializer.Serialize(new
                {
                    raw = rawElement,
                    extras = extrasElement
                }, JsonOpts);

                // Persist extraction (append-only)
                db.ReceiptExtractions.Add(new ReceiptExtraction
                {
                    TenantId = tenantId,
                    ReceiptId = receipt.Id,
                    ModelVersion = extractor.ModelVersion,
                    RawJson = wrappedRawJson,
                    NormalizedFieldsJson = normalizedFieldsJson,
                    Confidence = policyConfidence,
                    ExtractedAt = DateTimeOffset.UtcNow
                });

                // Always audit extraction completion (even if HOLD)
                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "receipt.extraction.completed",
                    EntityType = "Receipt",
                    EntityId = receipt.Id.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        modelVersion = extractor.ModelVersion,
                        policyConfidence,
                        extractorConfidence = normalized.Confidence
                    }, JsonOpts)
                });

                if (isHold)
                {
                    receipt.Status = "HOLD";

                    // Idempotent-ish: ensure a review exists
                    var existingReview = await db.ReceiptReviews
                        .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ReceiptId == receipt.Id, ct);

                    if (existingReview is null)
                    {
                        db.ReceiptReviews.Add(new ReceiptReview
                        {
                            TenantId = tenantId,
                            ReceiptId = receipt.Id,
                            HoldReason = holdReason,
                            QuestionsJson = questionsJson
                        });
                    }

                    // Idempotent-ish: avoid duplicate "new" notifications
                    var hasActiveNotif = await db.Notifications.AnyAsync(x =>
                        x.TenantId == tenantId &&
                        x.Type == "ReceiptHold" &&
                        x.Status == "New" &&
                        x.DataJson.Contains(receipt.Id.ToString("D")), ct);

                    if (!hasActiveNotif)
                    {
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
                    }

                    db.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        ActorUserId = "system",
                        Action = "receipt.hold",
                        EntityType = "Receipt",
                        EntityId = receipt.Id.ToString("D"),
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = correlationId,
                        MetadataJson = JsonSerializer.Serialize(new { holdReason, policyConfidence }, JsonOpts)
                    });
                }
                else
                {
                    receipt.Status = "ReadyForPosting";

                    db.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        ActorUserId = "system",
                        Action = "receipt.ready",
                        EntityType = "Receipt",
                        EntityId = receipt.Id.ToString("D"),
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = correlationId,
                        MetadataJson = JsonSerializer.Serialize(new { policyConfidence }, JsonOpts)
                    });
                }

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // 1) Always emit receipt.extracted.v1
                var extractedPayload = new ReceiptExtractedV1(
                    ReceiptId: receipt.Id,
                    FileObjectId: receipt.FileObjectId,
                    Confidence: policyConfidence,
                    IsHold: isHold,
                    HoldReason: holdReason
                );

                var extractedEnvelope = new MessageEnvelope<ReceiptExtractedV1>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "receipt.extracted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: extractedPayload
                );

                await publisher.PublishAsync("q.receipt.extracted", extractedEnvelope, ct);

                // 2) Emit explicit hold/ready workflow events
                if (isHold)
                {
                    var holdPayload = new ReceiptHoldV1(
                        ReceiptId: receipt.Id,
                        FileObjectId: receipt.FileObjectId,
                        Confidence: policyConfidence,
                        HoldReason: holdReason,
                        QuestionsJson: questionsJson
                    );

                    var holdEnvelope = new MessageEnvelope<ReceiptHoldV1>(
                        MessageId: Guid.NewGuid().ToString("N"),
                        Type: "receipt.hold.v1",
                        OccurredAt: DateTimeOffset.UtcNow,
                        TenantId: tenantId,
                        CorrelationId: correlationId,
                        Data: holdPayload
                    );

                    await publisher.PublishAsync("q.receipt.hold", holdEnvelope, ct);
                }
                else
                {
                    var readyPayload = new ReceiptReadyV1(
                        ReceiptId: receipt.Id,
                        FileObjectId: receipt.FileObjectId,
                        Confidence: policyConfidence
                    );

                    var readyEnvelope = new MessageEnvelope<ReceiptReadyV1>(
                        MessageId: Guid.NewGuid().ToString("N"),
                        Type: "receipt.ready.v1",
                        OccurredAt: DateTimeOffset.UtcNow,
                        TenantId: tenantId,
                        CorrelationId: correlationId,
                        Data: readyPayload
                    );

                    await publisher.PublishAsync("q.receipt.ready", readyEnvelope, ct);
                }
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

        // ----------------------------
        // Helpers: safe JSON embedding
        // ----------------------------

        // If it parses as JSON, return the JsonElement. If not, wrap as a string.
        private static object SafeParseJsonElementOrString(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return "";

            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch
            {
                return json!;
            }
        }

        // If it parses as JSON, return JsonElement; otherwise return null (extras are optional).
        private static object? SafeParseJsonElementOrNull(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }
    }
}
