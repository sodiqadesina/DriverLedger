using DriverLedger.Application.Auditing;
using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace DriverLedger.Api.Modules.Receipts
{
    public static class ApiReceipts
    {
        public static void MapReceiptEndpoints(WebApplication app)
        {
            var group = app.MapGroup("/receipts").WithTags("Receipts").RequireAuthorization("RequireDriver");

            group.MapGet("/", async (
                [FromQuery] string? status,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var q = db.Receipts.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(status))
                    q = q.Where(r => r.Status == status);

                var items = await q
                    .OrderByDescending(r => r.Id)
                    .Select(r => new
                    {
                        r.Id,
                        r.Status,
                        r.FileObjectId,
                        r.CreatedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(items);
            });

            //  NEW: Upload + create receipt + submit immediately (single route)
            group.MapPost("/upload", async (
                [FromForm] IFormFile file,
                DriverLedgerDbContext db,
                IBlobStorage blobStorage,
                IMessagePublisher publisher,
                IAuditWriter audit,
                HttpContext ctx,
                CancellationToken ct) =>
            {
                var tenantIdStr = ctx.User.FindFirstValue("tenantId");
                if (string.IsNullOrWhiteSpace(tenantIdStr) || !Guid.TryParse(tenantIdStr, out var tenantId))
                    return Results.Unauthorized();

                if (file is null || file.Length == 0)
                    return Results.BadRequest("Missing file.");

                // Allow common receipt types (adjust as needed)
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "application/pdf",
                    "image/jpeg",
                    "image/png",
                    "image/webp"
                };

                if (!allowed.Contains(file.ContentType))
                    return Results.BadRequest("Unsupported content type.");

                // Read once into bytes
                byte[] bytes;
                await using (var input = file.OpenReadStream())
                await using (var ms = new MemoryStream())
                {
                    await input.CopyToAsync(ms, ct);
                    bytes = ms.ToArray();
                }

                // sha256
                string sha256;
                using (var sha = SHA256.Create())
                using (var hashStream = new MemoryStream(bytes, writable: false))
                {
                    var hash = sha.ComputeHash(hashStream);
                    sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                // DEDUPE by exact file hash (tenant-scoped)
                var existingFile = await db.FileObjects
                    .Where(x => x.TenantId == tenantId
                             && x.Source == "ReceiptUpload"
                             && x.Sha256 == sha256)
                    .Select(x => new { x.Id, x.BlobPath })
                    .FirstOrDefaultAsync(ct);

                if (existingFile is not null)
                {
                    // If you also want to return the receipt id, you can query Receipts by FileObjectId here.
                    var existingReceipt = await db.Receipts
                        .Where(r => r.TenantId == tenantId && r.FileObjectId == existingFile.Id)
                        .Select(r => new { r.Id, r.Status })
                        .FirstOrDefaultAsync(ct);

                    return Results.Conflict(new
                    {
                        error = "Duplicate receipt upload detected",
                        message = "This exact receipt file was already uploaded.",
                        sha256,
                        existingFileObjectId = existingFile.Id,
                        existingReceiptId = existingReceipt?.Id,
                        existingReceiptStatus = existingReceipt?.Status
                    });
                }

                // Deterministic blob path (prevents duplicate blob objects)
                var ext = GuessExtension(file.ContentType, file.FileName);
                var blobPath = $"{tenantId}/receipts/{sha256}{ext}";

                using (var uploadStream = new MemoryStream(bytes, writable: false))
                {
                    await blobStorage.UploadAsync(blobPath, uploadStream, file.ContentType, ct);
                }

                var fileObject = new FileObject
                {
                    TenantId = tenantId,
                    BlobPath = blobPath,
                    Sha256 = sha256,
                    Size = file.Length,
                    ContentType = file.ContentType,
                    OriginalName = file.FileName,
                    Source = "ReceiptUpload"
                };

                db.FileObjects.Add(fileObject);
                await db.SaveChangesAsync(ct);

                // Create receipt + immediately submit
                var receipt = new Receipt
                {
                    TenantId = tenantId,
                    FileObjectId = fileObject.Id,
                    Status = "Submitted"
                };

                db.Receipts.Add(receipt);
                await db.SaveChangesAsync(ct);

                var correlationId =
                    (ctx.Items["x-correlation-id"]?.ToString()) ??
                    Guid.NewGuid().ToString("N");

                // Publish to queue (single consumer pipeline)
                var envelope = new MessageEnvelope<ReceiptReceived>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "receipt.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: receipt.TenantId,
                    CorrelationId: correlationId,
                    Data: new ReceiptReceived(receipt.Id, receipt.FileObjectId)
                );

                await publisher.PublishAsync("q.receipt.received", envelope, ct);

                // Immediately mark processing (matches your existing submit behavior)
                receipt.Status = "Processing";
                await db.SaveChangesAsync(ct);

                await audit.WriteAsync(
                    action: "receipt.submitted",
                    entityType: "Receipt",
                    entityId: receipt.Id.ToString("D"),
                    metadata: new { receipt.FileObjectId, receipt.Status },
                    ct: ct);

                return Results.Accepted(
                value: new
                {
                    receiptId = receipt.Id,
                    status = receipt.Status,
                    fileObjectId = receipt.FileObjectId,
                    sha256
                });
            }).DisableAntiforgery();

            // Keep the old "create receipt from existing fileObject" route if you still need it
            group.MapPost("/", async (CreateReceiptRequest req, DriverLedgerDbContext db, HttpContext ctx, CancellationToken ct) =>
            {
                var tenantId = Guid.Parse(ctx.User.FindFirstValue("tenantId")!);

                // Ensure file exists (tenant-filtered automatically)
                var fileExists = await db.FileObjects.FindAsync([req.FileObjectId], ct) is not null;
                if (!fileExists) return Results.BadRequest("Invalid fileObjectId.");

                var receipt = new Receipt
                {
                    TenantId = tenantId,
                    FileObjectId = req.FileObjectId,
                    Status = "Draft"
                };

                db.Receipts.Add(receipt);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new { receiptId = receipt.Id, status = receipt.Status });
            });

            // Existing submit endpoint (still useful; now mostly optional)
            group.MapPost("/{id:guid}/submit", async (Guid id, DriverLedgerDbContext db, IMessagePublisher publisher, IAuditWriter audit, HttpContext ctx, CancellationToken ct) =>
            {
                var receipt = await db.Receipts.FindAsync([id], ct);
                if (receipt is null) return Results.NotFound();

                if (receipt.Status != "Draft")
                    return Results.BadRequest("Receipt is not in Draft state.");

                receipt.Status = "Submitted";
                await db.SaveChangesAsync(ct);

                var correlationId = (ctx.Items["x-correlation-id"]?.ToString()) ?? Guid.NewGuid().ToString("N");

                var envelope = new MessageEnvelope<ReceiptReceived>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "receipt.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: receipt.TenantId,
                    CorrelationId: correlationId,
                    Data: new ReceiptReceived(receipt.Id, receipt.FileObjectId)
                );

                await publisher.PublishAsync("q.receipt.received", envelope, ct);

                receipt.Status = "Processing";
                await db.SaveChangesAsync(ct);

                await audit.WriteAsync(
                    action: "receipt.submitted",
                    entityType: "Receipt",
                    entityId: receipt.Id.ToString("D"),
                    metadata: new { receipt.FileObjectId, receipt.Status },
                    ct: ct);

                return Results.Ok(new { receiptId = receipt.Id, status = receipt.Status });
            });

            group.MapPost("/{id:guid}/review/resolve",
            async (
                Guid id,
                ResolveReceiptReviewRequest req,
                DriverLedgerDbContext db,
                IMessagePublisher publisher,
                IRequestContext requestContext,
                CancellationToken ct) =>
            {
                var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.Id == id, ct);
                if (receipt is null) return Results.NotFound();

                var review = await db.ReceiptReviews.SingleOrDefaultAsync(x => x.ReceiptId == id, ct);
                if (review is null) return Results.NotFound(new { message = "No review exists for this receipt." });

                review.ResolutionJson = req.ResolutionJson;
                review.ResolvedAt = DateTimeOffset.UtcNow;

                var userId = requestContext.UserId;
                if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var userGuid))
                    review.ResolvedByUserId = userGuid;

                if (req.Resubmit)
                {
                    receipt.Status = "Processing";
                    var correlationId = requestContext.CorrelationId ?? Guid.NewGuid().ToString("N");

                    var envelope = new MessageEnvelope<ReceiptReceived>(
                        MessageId: Guid.NewGuid().ToString("N"),
                        Type: "receipt.received.v1",
                        OccurredAt: DateTimeOffset.UtcNow,
                        TenantId: receipt.TenantId,
                        CorrelationId: correlationId,
                        Data: new ReceiptReceived(receipt.Id, receipt.FileObjectId)
                    );

                    await publisher.PublishAsync("q.receipt.received", envelope, ct);
                }
                else
                {
                    receipt.Status = "ReadyForPosting";
                }

                await db.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    receiptId = receipt.Id,
                    receiptStatus = receipt.Status,
                    resolvedBy = userId
                });
            });
        }

        public sealed record CreateReceiptRequest(Guid FileObjectId);

        private static string GuessExtension(string contentType, string fileName)
        {
            // prefer the original extension if present
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8)
                return ext;

            return contentType.ToLowerInvariant() switch
            {
                "application/pdf" => ".pdf",
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".bin"
            };
        }
    }
}
