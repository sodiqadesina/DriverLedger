using DriverLedger.Application.Messaging;
using DriverLedger.Domain.Receipts;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using System.Security.Claims;

namespace DriverLedger.Api.Modules.Receipts
{
    public static class ApiReceipts
    {
        public static void MapReceiptEndpoints(WebApplication app)
        {
            var group = app.MapGroup("/receipts").WithTags("Receipts").RequireAuthorization("RequireDriver");

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

            group.MapPost("/{id:guid}/submit", async (Guid id, DriverLedgerDbContext db, IMessagePublisher publisher, HttpContext ctx, CancellationToken ct) =>
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

                return Results.Ok(new { receiptId = receipt.Id, status = receipt.Status });
            });
        }

        public sealed record CreateReceiptRequest(Guid FileObjectId);
        public sealed record ReceiptReceived(Guid ReceiptId, Guid FileObjectId);
    }
}
