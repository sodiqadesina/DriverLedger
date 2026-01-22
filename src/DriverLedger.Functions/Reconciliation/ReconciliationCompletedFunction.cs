using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Infrastructure.Ledger;
using System.Text.Json;

namespace DriverLedger.Functions.Reconciliation
{
    /// <summary>
    /// Queue trigger that posts reconciliation variances into the ledger.
    /// </summary>
    public sealed class ReconciliationCompletedFunction(
        ReconciliationToLedgerPostingHandler handler,
        ILogger<ReconciliationCompletedFunction> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        [Function(nameof(ReconciliationCompletedFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.reconciliation.completed", Connection = "Azure:ServiceBusConnectionString")]
            string body,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReconciliationCompleted>>(body, JsonOpts);
            if (envelope is null)
            {
                logger.LogWarning("Failed to deserialize reconciliation.completed message.");
                return;
            }

            await handler.HandleAsync(envelope, ct);
        }
    }
}
