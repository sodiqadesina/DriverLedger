

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
        public async Task Run(
            [ServiceBusTrigger("q.receipt.extracted", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptExtractedV1>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                _log.LogError("Invalid receipt.extracted payload.");
                return;
            }

            await _handler.HandleAsync(envelope, ct);
        }
    }
}
