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
            var group = app.MapGroup("/api/statements")
                .WithTags("Statements")
                .RequireAuthorization("RequireDriver");

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
                var metadataExtractor = SelectMetadataExtractor(metadataExtractors, file.ContentType);

                StatementMetadataResult metadata;
                using (var extractionStream = new MemoryStream(bytes, writable: false))
                {
                    metadata = await metadataExtractor.ExtractAsync(extractionStream, ct);
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

                var periodType = (metadata.PeriodType ?? "Unknown").Trim();
                var periodKey = (metadata.PeriodKey ?? "Unknown").Trim();
                var periodStart = metadata.PeriodStart ?? DateOnly.MinValue;
                var periodEnd = metadata.PeriodEnd ?? DateOnly.MinValue;

                // ---------------------------
                // DEDUPE (before blob/DB)
                // ---------------------------

                // Business-key dedupe: same provider+periodType+periodKey for the same tenant
                var existingStatement = await db.Statements
                    .Where(x => x.TenantId == tenantId
                             && x.Provider == provider
                             && x.PeriodType == periodType
                             && x.PeriodKey == periodKey)
                    .Select(x => new { x.Id, x.Status })
                    .FirstOrDefaultAsync(ct);

                if (existingStatement is not null)
                {
                    return Results.Conflict(new
                    {
                        error = "Duplicate statement detected",
                        message = $"A {provider} {periodType} statement for period '{periodKey}' already exists.",
                        provider,
                        periodType,
                        periodKey,
                        existingStatementId = existingStatement.Id,
                        existingStatus = existingStatement.Status
                    });
                }

                // Exact-file dedupe: same sha256 already uploaded for this tenant
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
                // Most granular wins (posting rule), BUT always store the statement.
                // Use PeriodStart.Year (EF-safe + consistent).
                // ---------------------------

                var year = periodStart.Year;
                var incomingRank = GetGranularityRank(periodType);

                var existingTypesThisYear = await db.Statements
                    .Where(s => s.TenantId == tenantId
                             && s.Provider == provider
                             && s.PeriodStart.Year == year)
                    .Select(s => s.PeriodType)
                    .ToListAsync(ct);

                var mostGranularExistingRank = existingTypesThisYear.Count == 0
                    ? 0
                    : existingTypesThisYear.Max(GetGranularityRank);

                // If incoming is less granular than what's already present -> store only (reconciliation)
                var shouldAutoSubmit = incomingRank >= mostGranularExistingRank;

                // ---------------------------
                // Deterministic blob path
                // ---------------------------
                var safeProvider = Sanitize(provider).Replace(' ', '_');
                var safePeriodKey = Sanitize(periodKey).Replace(' ', '_');
                var blobPath = $"{tenantId}/statements/{safeProvider}/{safePeriodKey}/{sha256}.pdf";

                // Upload to blob
                using (var uploadStream = new MemoryStream(bytes, writable: false))
                {
                    await blobStorage.UploadAsync(blobPath, uploadStream, file.ContentType, ct);
                }

                // Create FileObject
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

                // Create Statement (always stored)
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

                // --------------------------------------------------------------------
                // Always publish statement.received so extraction runs and StatementLines
                // are persisted for both:
                // - Submitted statements (posted to ledger)
                // - ReconciliationOnly statements (stored for reconciliation, NOT posted)
                // --------------------------------------------------------------------
                var receivedPayload = new StatementReceived(
                    StatementId: statement.Id,
                    TenantId: statement.TenantId,
                    Provider: statement.Provider,
                    PeriodType: statement.PeriodType,
                    PeriodKey: statement.PeriodKey,
                    FileObjectId: statement.FileObjectId,
                    PeriodStart: statement.PeriodStart,
                    PeriodEnd: statement.PeriodEnd
                );

                var receivedEnvelope = new MessageEnvelope<StatementReceived>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "statement.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: Guid.NewGuid().ToString("N"),
                    Data: receivedPayload
                );

                // If it loses to a more granular existing statement, keep it saved only
                if (!shouldAutoSubmit)
                {
                    // ReconciliationOnly: extract lines, but do not submit to ledger pipeline.
                    statement.Status = "ReconciliationOnly";
                    await db.SaveChangesAsync(ct);

                    await publisher.PublishAsync("q.statement.received", receivedEnvelope, ct);

                    return Results.Ok(new
                    {
                        statementId = statement.Id,
                        status = statement.Status,
                        message =
                            "Statement uploaded for reconciliation. Extraction will run to populate statement lines, " +
                            "but it will not be posted to the ledger due to granularity rules.",
                        provider,
                        periodType,
                        periodKey,
                        year,
                        extractedToLines = true,
                        postedToLedger = false
                    });
                }

                // Auto-submit (post to pipeline)
                statement.Status = "Submitted";
                await db.SaveChangesAsync(ct);

                await publisher.PublishAsync("q.statement.received", receivedEnvelope, ct);

                return Results.Accepted($"/api/statements/{statement.Id}", new
                {
                    statementId = statement.Id,
                    status = statement.Status,
                    postedToLedger = true
                });
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

                // If already reconciliation-only, block submission
                if (string.Equals(statement.Status, "ReconciliationOnly", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new
                    {
                        error = "Posting blocked by granularity rules",
                        message =
                            "This statement is stored for reconciliation only and cannot be submitted because " +
                            "a more granular statement exists for the same year.",
                        statementId = statement.Id,
                        provider = statement.Provider,
                        periodType = statement.PeriodType,
                        periodKey = statement.PeriodKey
                    });
                }

                // Idempotent-ish: if already submitted, just return accepted
                if (string.Equals(statement.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
                    return Results.Accepted($"/api/statements/{statement.Id}", new { statement.Id });

                // Enforce "most granular wins" at submit time too
                var year = statement.PeriodStart.Year;
                var incomingRank = GetGranularityRank(statement.PeriodType);

                var otherTypesThisYear = await db.Statements
                    .Where(s => s.TenantId == tenantId
                             && s.Provider == statement.Provider
                             && s.Id != statement.Id
                             && s.PeriodStart.Year == year)
                    .Select(s => s.PeriodType)
                    .ToListAsync(ct);

                var mostGranularExistingRank = otherTypesThisYear.Count == 0
                    ? 0
                    : otherTypesThisYear.Max(GetGranularityRank);

                if (incomingRank < mostGranularExistingRank)
                {
                    statement.Status = "ReconciliationOnly";
                    await db.SaveChangesAsync(ct);

                    return Results.Conflict(new
                    {
                        error = "Posting blocked by granularity rules",
                        message =
                            "A more granular statement already exists for this year. This statement will be kept " +
                            "for reconciliation only and will not be posted to the ledger.",
                        statementId = statement.Id,
                        provider = statement.Provider,
                        periodType = statement.PeriodType,
                        periodKey = statement.PeriodKey,
                        year
                    });
                }

                statement.Status = "Submitted";
                await db.SaveChangesAsync(ct);

                var receivedPayload = new StatementReceived(
                    StatementId: statement.Id,
                    TenantId: statement.TenantId,
                    Provider: statement.Provider,
                    PeriodType: statement.PeriodType,
                    PeriodKey: statement.PeriodKey,
                    FileObjectId: statement.FileObjectId,
                    PeriodStart: statement.PeriodStart,
                    PeriodEnd: statement.PeriodEnd
                );

                var receivedEnvelope = new MessageEnvelope<StatementReceived>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "statement.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: Guid.NewGuid().ToString("N"),
                    Data: receivedPayload
                );

                await publisher.PublishAsync("q.statement.received", receivedEnvelope, ct);

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

        // Granularity: Monthly (3) > Quarterly (2) > YTD/Yearly (1)
        private static int GetGranularityRank(string? periodType)
        {
            if (string.IsNullOrWhiteSpace(periodType)) return 0;

            return periodType.Trim().ToLowerInvariant() switch
            {
                "monthly" => 3,
                "quarterly" => 2,
                "ytd" => 1,
                "yearly" => 1,
                _ => 0
            };
        }
    }
}
