using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace DriverLedger.Api.Modules.Statements

{
    public static class ApiStatements
    {
        public static IEndpointRouteBuilder MapStatementEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/statements").WithTags("Statements");

            // Upload a statement file + metadata
            group.MapPost("/upload", async (
                [FromForm] IFormFile file,
                [FromForm] string provider,
                [FromForm] string periodType,
                [FromForm] string periodKey,
                [FromForm] DateOnly periodStart,
                [FromForm] DateOnly periodEnd,
                ITenantProvider tenantProvider,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;

                // TODO: create FileObject from file (use existing file upload pattern from receipts)
                var fileObjectId = Guid.NewGuid(); // replace with real FileObject ID

                var statement = new Statement
                {
                    TenantId = tenantId,
                    FileObjectId = fileObjectId,
                    Provider = provider,
                    PeriodType = periodType,
                    PeriodKey = periodKey,
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    Status = "Uploaded"
                };

                db.Statements.Add(statement);
                await db.SaveChangesAsync(ct);

                return Results.Created($"/api/statements/{statement.Id}", new { statement.Id });
            });

            // Submit a statement for processing
            group.MapPost("/{statementId:guid}/submit", async (
                Guid statementId,
                ITenantProvider tenantProvider,
                DriverLedgerDbContext db,
                IMessagePublisher publisher,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;

                var statement = await db.Statements
                    .SingleOrDefaultAsync(x => x.Id == statementId && x.TenantId == tenantId, ct);

                if (statement is null)
                {
                    return Results.NotFound();
                }

                statement.Status = "Submitted";
                await db.SaveChangesAsync(ct);

                var payload = new StatementReceived(
                    StatementId: statement.Id,
                    TenantId: statement.TenantId,
                    Provider: statement.Provider,
                    PeriodType: statement.PeriodType,
                    PeriodKey: statement.PeriodKey,
                    FileObjectId: statement.FileObjectId,
                    PeriodStart: statement.PeriodStart,
                    PeriodEnd: statement.PeriodEnd
                );

                var envelope = new MessageEnvelope<StatementReceived>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "statement.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: Guid.NewGuid().ToString("N"),
                    Data: payload
                );

                await publisher.PublishAsync("q.statement.received", envelope, ct);

                return Results.Accepted($"/api/statements/{statement.Id}");
            });


            // List statements
            group.MapGet("/", async (
                ITenantProvider tenantProvider,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;

                var items = await db.Statements
                    .Where(x => x.TenantId == tenantId)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => new
                    {
                        x.Id,
                        x.Provider,
                        x.PeriodType,
                        x.PeriodKey,
                        x.PeriodStart,
                        x.PeriodEnd,
                        x.Status,
                        x.CreatedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(items);
            });

            return app;
        }
    }
}
