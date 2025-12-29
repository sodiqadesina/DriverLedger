

using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Infrastructure.Ledger;
using System.Text.Json;

namespace DriverLedger.Functions.Receipts
{
    public sealed class ReceiptExtractedFunction
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private readonly ReceiptToLedgerPostingHandler _handler;
        private readonly ILogger<ReceiptExtractedFunction> _log;

        public ReceiptExtractedFunction(ReceiptToLedgerPostingHandler handler, ILogger<ReceiptExtractedFunction> log)
        {
            _handler = handler;
            _log = log;
        }

        [Function(nameof(ReceiptExtractedFunction))]
        public Task Run(
     [ServiceBusTrigger("q.receipt.extracted", Connection = "Azure:ServiceBusConnectionString")]
    string messageJson,
     CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptExtractedV1>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                _log.LogError("Invalid receipt.extracted payload.");
                return Task.CompletedTask;
            }

            using var scope = _log.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = envelope.TenantId,
                ["correlationId"] = envelope.CorrelationId,
                ["messageId"] = envelope.MessageId,
                ["receiptId"] = envelope.Data.ReceiptId,
                ["isHold"] = envelope.Data.IsHold
            });

            _log.LogInformation(
                "receipt.extracted consumed for analytics only. Posting is handled by receipt.ready/receipt.hold workflows.");

            return Task.CompletedTask;
        }

    }
}
