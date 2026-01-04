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
            var group = app.MapGroup("/api/statements").WithTags("Statements").RequireAuthorization("RequireDriver");

            // Upload a statement file + metadata
            group.MapPost("/upload", async (
                [FromForm] IFormFile file,
                ITenantProvider tenantProvider,
                IEnumerable<IStatementMetadataExtractor> metadataExtractors,
                IBlobStorage blobStorage,
                DriverLedgerDbContext db,
                IMessagePublisher publisher,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;

                if (file is null || file.Length == 0)
                    return Results.BadRequest("Missing file.");

                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "application/pdf" };

                if (!allowed.Contains(file.ContentType))
                    return Results.BadRequest("Unsupported content type.");

                // Read once into bytes (safe even if downstream disposes streams)
                byte[] bytes;
                await using (var input = file.OpenReadStream())
                await using (var ms = new MemoryStream())
                {
                    await input.CopyToAsync(ms, ct);
                    bytes = ms.ToArray();
                }

                // Hash (fresh stream)
                string sha256;
                using (var sha = SHA256.Create())
                using (var hashStream = new MemoryStream(bytes, writable: false))
                {
                    var hash = sha.ComputeHash(hashStream);
                    sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                // Metadata extraction FIRST (fresh stream)
                var extractor = SelectMetadataExtractor(metadataExtractors, file.ContentType);

                StatementMetadataResult metadata;
                using (var extractionStream = new MemoryStream(bytes, writable: false))
                {
                    metadata = await extractor.ExtractAsync(extractionStream, ct);
                }

                // Provider validation (Uber/Lyft only)
                var provider = NormalizeProvider(metadata.Provider);
                if (!IsRecognizedProvider(provider))
                {
                    return Results.BadRequest(new
                    {
                        error = "Unrecognized statement provider. Only Uber/Lyft statements are supported.",
                        detectedProvider = provider,
                        supportedProviders = RecognizedProviders.ToArray()
                    });
                }

                var periodType = metadata.PeriodType ?? "Unknown";
                var periodKey = metadata.PeriodKey ?? "Unknown";
                var periodStart = metadata.PeriodStart ?? DateOnly.MinValue;
                var periodEnd = metadata.PeriodEnd ?? DateOnly.MinValue;

                // ---------------------------
                //  DEDUPE (before blob/DB)
                // ---------------------------

                // 1) Business-key dedupe: same provider+period for the same tenant
                var existingStatement = await db.Statements
                    .Where(x => x.TenantId == tenantId
                             && x.Provider == provider
                             && x.PeriodKey == periodKey)
                    .Select(x => new { x.Id, x.Status, x.FileObjectId })
                    .FirstOrDefaultAsync(ct);

                if (existingStatement is not null)
                {
                    return Results.Conflict(new
                    {
                        error = "Duplicate statement detected",
                        message = $"A {provider} statement for period '{periodKey}' already exists.",
                        provider,
                        periodKey,
                        existingStatementId = existingStatement.Id,
                        existingStatus = existingStatement.Status
                    });
                }

                // 2) Exact-file dedupe: same sha256 already uploaded for this tenant
                var existingFile = await db.FileObjects
                    .Where(x => x.TenantId == tenantId
                             && x.Source == "StatementUpload"
                             && x.Sha256 == sha256)
                    .Select(x => new { x.Id, x.BlobPath })
                    .FirstOrDefaultAsync(ct);

                if (existingFile is not null)
                {
                    return Results.Conflict(new
                    {
                        error = "Duplicate file detected",
                        message = "This exact statement file was already uploaded.",
                        sha256,
                        existingFileObjectId = existingFile.Id,
                        existingBlobPath = existingFile.BlobPath
                    });
                }

                // ---------------------------
                //  Deterministic blob path
                // (prevents duplicate blob objects)
                // ---------------------------
                var safeProvider = Sanitize(provider).Replace(' ', '_');
                var safePeriodKey = Sanitize(periodKey).Replace(' ', '_');
                var blobPath = $"{tenantId}/statements/{safeProvider}/{safePeriodKey}/{sha256}.pdf";

                // Only persist after validation + dedupe
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
                    Source = "StatementUpload"
                };

                db.FileObjects.Add(fileObject);
                await db.SaveChangesAsync(ct);

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

                // Auto-submit (same as /submit)
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

                return Results.Accepted($"/api/statements/{statement.Id}", new { statement.Id });
            }).DisableAntiforgery();


            // Submit a statement for processing with id (assumes already uploaded)
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
                    return Results.NotFound();

                //  idempotent-ish: if already submitted, just return accepted
                if (string.Equals(statement.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
                    return Results.Accepted($"/api/statements/{statement.Id}", new { statement.Id });

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

                return Results.Accepted($"/api/statements/{statement.Id}", new { statement.Id });
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

        private static readonly HashSet<string> RecognizedProviders =
            new(StringComparer.OrdinalIgnoreCase) { "Uber", "Lyft" };

        private static string NormalizeProvider(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return "Unknown";

            if (provider.Contains("uber", StringComparison.OrdinalIgnoreCase)) return "Uber";
            if (provider.Contains("lyft", StringComparison.OrdinalIgnoreCase)) return "Lyft";

            return provider.Trim();
        }

        private static bool IsRecognizedProvider(string provider)
            => RecognizedProviders.Contains(provider);
    }
}
