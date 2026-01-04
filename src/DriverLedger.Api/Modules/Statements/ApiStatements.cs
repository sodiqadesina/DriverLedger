using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Statements.Extraction;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

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
                ITenantProvider tenantProvider,
                IEnumerable<IStatementMetadataExtractor> metadataExtractors,
                IBlobStorage blobStorage,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;

                if (file is null || file.Length == 0)
                    return Results.BadRequest("Missing file.");

                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "application/pdf" };

                if (!allowed.Contains(file.ContentType))
                    return Results.BadRequest("Unsupported content type.");

                string sha256;
                await using (var s = file.OpenReadStream())
                {
                    using var sha = SHA256.Create();
                    var hash = await sha.ComputeHashAsync(s, ct);
                    sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                var blobPath = $"{tenantId}/statements/{Guid.NewGuid():N}-{Sanitize(file.FileName)}";

                await using (var uploadStream = file.OpenReadStream())
                    await blobStorage.UploadAsync(blobPath, uploadStream, file.ContentType, ct);

                var fileObject = new FileObject
                {
                    TenantId = tenantId,
                    BlobPath = blobPath,
                    Sha256 = sha256,
                    Size = file.Length,
                    ContentType = file.ContentType,
                    OriginalName = file.FileName,
                    Source = "StatementUpload"
                };

                db.FileObjects.Add(fileObject);
                await db.SaveChangesAsync(ct);

                var extractor = SelectMetadataExtractor(metadataExtractors, file.ContentType);

                StatementMetadataResult metadata;
                await using (var extractionStream = file.OpenReadStream())
                {
                    metadata = await extractor.ExtractAsync(extractionStream, ct);
                }

                var provider = metadata.Provider ?? "Unknown";
                var periodType = metadata.PeriodType ?? "Unknown";
                var periodKey = metadata.PeriodKey ?? "Unknown";
                var periodStart = metadata.PeriodStart ?? DateOnly.MinValue;
                var periodEnd = metadata.PeriodEnd ?? DateOnly.MinValue;

                var statement = new Statement
                {
                    TenantId = tenantId,
                    FileObjectId = fileObject.Id,
                    Provider = provider,
                    PeriodType = periodType,
                    PeriodKey = periodKey,
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    VendorName = metadata.VendorName,
                    StatementTotalAmount = metadata.StatementTotalAmount,
                    TaxAmount = metadata.TaxAmount,
                    CurrencyCode = metadata.Currency,
                    Status = "Uploaded"
                };

                db.Statements.Add(statement);
                await db.SaveChangesAsync(ct);

                return Results.Created($"/api/statements/{statement.Id}", new { statement.Id });
            }).DisableAntiforgery();

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
                        x.VendorName,
                        x.StatementTotalAmount,
                        x.TaxAmount,
                        x.CurrencyCode,
                        x.Status,
                        x.CreatedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(items);
            });

            return app;
        }

        private static IStatementMetadataExtractor SelectMetadataExtractor(
            IEnumerable<IStatementMetadataExtractor> extractors,
            string contentType)
        {
            var extractor = extractors.FirstOrDefault(x => x.CanHandleContentType(contentType));
            if (extractor is null)
            {
                throw new InvalidOperationException($"No statement metadata extractor registered for content type '{contentType}'.");
            }

            return extractor;
        }

        private static string Sanitize(string name)
            => string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ')).Trim();
    }
}
