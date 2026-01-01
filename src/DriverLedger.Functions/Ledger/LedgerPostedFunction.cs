

using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Statements.Snapshots;
using System.Text.Json;

namespace DriverLedger.Functions.Ledger

{

    public sealed class LedgerPostedFunction
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private readonly SnapshotCalculator _calculator;
        private readonly ILogger<LedgerPostedFunction> _log;

        public LedgerPostedFunction(SnapshotCalculator calculator, ILogger<LedgerPostedFunction> log)
        {
            _calculator = calculator;
            _log = log;
        }

        [Function(nameof(LedgerPostedFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.ledger.posted", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<LedgerPosted>>(messageJson, JsonOpts);
            if (envelope is null)
            {
                _log.LogError("Invalid ledger.posted message (cannot deserialize).");
                return;
            }

            await _calculator.HandleAsync(envelope, ct);
        }
    }
}
