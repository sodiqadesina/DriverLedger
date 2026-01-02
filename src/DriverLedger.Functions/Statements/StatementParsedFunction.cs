

using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Infrastructure.Ledger;
using System.Text.Json;

namespace DriverLedger.Functions.Statements
{
    public sealed class StatementParsedFunction
    {
        private readonly StatementToLedgerPostingHandler _handler;
        private readonly ILogger<StatementParsedFunction> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public StatementParsedFunction(
            StatementToLedgerPostingHandler handler,
            ILogger<StatementParsedFunction> log)
        {
            _handler = handler;
            _log = log;
        }

        [Function(nameof(StatementParsedFunction))]
        public async Task Run(
            [ServiceBusTrigger("q.statement.parsed", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
            FunctionContext context,
            CancellationToken ct)
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<StatementParsed>>(messageJson, JsonOpts);
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

            await _handler.HandleAsync(envelope, ct);
        }
    }
}
