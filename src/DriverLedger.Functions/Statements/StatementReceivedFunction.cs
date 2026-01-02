

using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Infrastructure.Statements.Extraction;
using System.Text.Json;

namespace DriverLedger.Functions.Statements
{
    public sealed class StatementReceivedFunction
    {
        private readonly StatementExtractionHandler _extract;
        private readonly ILogger<StatementReceivedFunction> _log;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public StatementReceivedFunction(
            StatementExtractionHandler extract,
            ILogger<StatementReceivedFunction> log)
        {
            _extract = extract;
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

            await _extract.HandleAsync(envelope, ct);
        }
    }
}
