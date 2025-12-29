using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Ledger;

using System.Text.Json;

namespace DriverLedger.Functions.Receipts
{
    public sealed class ReceiptReadyFunction
    {
        private readonly ReceiptToLedgerPostingHandler _posting;
        private readonly ILogger<ReceiptReadyFunction> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public ReceiptReadyFunction(
            ReceiptToLedgerPostingHandler posting,
            ILogger<ReceiptReadyFunction> log)
        {
            _posting = posting;
            _log = log;
        }

        [Function(nameof(ReceiptReadyFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.receipt.ready", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            FunctionContext context,
            CancellationToken ct)
        {
            var readyEnvelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptReadyV1>>(messageJson, JsonOpts);
            if (readyEnvelope is null)
            {
                _log.LogError("Invalid READY message payload (cannot deserialize).");
                return;
            }

            using var scope = _log.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = readyEnvelope.TenantId,
                ["correlationId"] = readyEnvelope.CorrelationId,
                ["messageId"] = readyEnvelope.MessageId,
                ["receiptId"] = readyEnvelope.Data.ReceiptId
            });

            // Change HoldReason: null to HoldReason: string.Empty to satisfy non-nullable reference type
            var extractedPayload = new ReceiptExtractedV1(
                ReceiptId: readyEnvelope.Data.ReceiptId,
                FileObjectId: readyEnvelope.Data.FileObjectId,
                Confidence: readyEnvelope.Data.Confidence,
                IsHold: false,
                HoldReason: string.Empty
            );

            var extractedEnvelope = new MessageEnvelope<ReceiptExtractedV1>(
                MessageId: Guid.NewGuid().ToString("N"),
                Type: "receipt.extracted.v1",
                OccurredAt: readyEnvelope.OccurredAt,
                TenantId: readyEnvelope.TenantId,
                CorrelationId: readyEnvelope.CorrelationId,
                Data: extractedPayload
            );

            await _posting.HandleAsync(extractedEnvelope, ct);
        }
    }
}
