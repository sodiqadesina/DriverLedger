using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Statements.Extraction;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DriverLedger.Functions.Statements
{
    public sealed class StatementReceivedFunction
    {
        private readonly StatementExtractionHandler _extract;
        private readonly DriverLedgerDbContext _db;
        private readonly ILogger<StatementReceivedFunction> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public StatementReceivedFunction(
            StatementExtractionHandler extract,
            DriverLedgerDbContext db,
            ILogger<StatementReceivedFunction> log)
        {
            _extract = extract;
            _db = db;
            _log = log;
        }

        [Function(nameof(StatementReceivedFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.statement.received", Connection = "Azure:ServiceBusConnectionString")]
            string messageJson,
            FunctionContext context,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<StatementReceived>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                _log.LogError("Invalid message payload (cannot deserialize).");
                return;
            }

            using var scope = _log.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = envelope.TenantId,
                ["correlationId"] = envelope.CorrelationId,
                ["messageId"] = envelope.MessageId,
                ["statementId"] = envelope.Data.StatementId
            });

            // Extra diagnostics: log statement + file metadata before extraction runs
            try
            {
                var tenantId = envelope.TenantId;
                var statementId = envelope.Data.StatementId;
                var fileObjectId = envelope.Data.FileObjectId;

                var stmt = await _db.Statements
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == statementId, ct);


                var fileObj = await _db.FileObjects
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == fileObjectId, ct);


                if (stmt is null)
                {
                    _log.LogWarning("Statement not found in DB. StatementId={StatementId}", statementId);
                }

                if (fileObj is null)
                {
                    _log.LogWarning("FileObject not found in DB. FileObjectId={FileObjectId}", fileObjectId);
                }
                else
                {
                    _log.LogInformation(
                        "StatementReceived diagnostics: FileObjectId={FileObjectId} BlobPath={BlobPath} Size={Size} ContentType={ContentType} Sha256={Sha256} OriginalName={OriginalName} Source={Source}",
                        fileObj.Id,
                        fileObj.BlobPath,
                        fileObj.Size,
                        fileObj.ContentType,
                        fileObj.Sha256,
                        fileObj.OriginalName,
                        fileObj.Source);

                    if (fileObj.Size == 0)
                        _log.LogWarning("FileObject size is 0 bytes. This will always fail extraction. BlobPath={BlobPath}", fileObj.BlobPath);

                    if (!string.IsNullOrWhiteSpace(fileObj.ContentType) &&
                        fileObj.ContentType.Contains("image", StringComparison.OrdinalIgnoreCase))
                        _log.LogInformation("ContentType indicates an image; statement may be a scanned image-only doc. ContentType={ContentType}", fileObj.ContentType);
                }

                if (stmt is not null)
                {
                    _log.LogInformation(
                        "Statement meta: Provider={Provider} PeriodType={PeriodType} PeriodKey={PeriodKey} PeriodStart={PeriodStart} PeriodEnd={PeriodEnd} Status={Status} Currency={Currency}",
                        stmt.Provider,
                        stmt.PeriodType,
                        stmt.PeriodKey,
                        stmt.PeriodStart,
                        stmt.PeriodEnd,
                        stmt.Status,
                        stmt.CurrencyCode);
                }
            }
            catch (Exception ex)
            {
                // Do not fail the pipeline because diagnostics failed
                _log.LogWarning(ex, "Failed to log statement/file metadata diagnostics.");
            }

            await _extract.HandleAsync(envelope, ct);
        }
    }
}
