using DriverLedger.Application.Auditing;
using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;

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

            group.MapPost("/{id:guid}/submit", async (Guid id, DriverLedgerDbContext db, IMessagePublisher publisher, IAuditWriter audit, HttpContext ctx, CancellationToken ct) =>
            {
                var receipt = await db.Receipts.FindAsync([id], ct);
                if (receipt is null) return Results.NotFound();

                if (receipt.Status != "Draft")
                    return Results.BadRequest("Receipt is not in Draft state.");

                receipt.Status = "Submitted";
                await db.SaveChangesAsync(ct);

                var correlationId = (ctx.Items["x-correlation-id"]?.ToString()) ?? Guid.NewGuid().ToString("N");

                // Publish to queue (single consumer pipeline)
                var envelope = new MessageEnvelope<ReceiptReceived>(
                    MessageId: Guid.NewGuid().ToString(),
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

                // If JWT sub is a GUID, persist it
                var userId = requestContext.UserId;
                if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var userGuid))
                    review.ResolvedByUserId = userGuid;

                if (req.Resubmit)
                {
                    receipt.Status = "Processing";

                    // correlation: if you want, reuse the request correlation
                    var correlationId = requestContext.CorrelationId ?? Guid.NewGuid().ToString("N");

                    var envelope = new DriverLedger.Application.Messaging.MessageEnvelope<DriverLedger.Application.Receipts.Messages.ReceiptReceived>(
                        MessageId: Guid.NewGuid().ToString("N"),
                        Type: "receipt.received.v1",
                        OccurredAt: DateTimeOffset.UtcNow,
                        TenantId: receipt.TenantId,
                        CorrelationId: correlationId,
                        Data: new DriverLedger.Application.Receipts.Messages.ReceiptReceived(receipt.Id, receipt.FileObjectId)
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
        
    }
}
