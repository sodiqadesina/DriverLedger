using System.Text.Json;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Messages;


namespace DriverLedger.Functions.Receipts;

public sealed class ReceiptReceivedFunction
{
    private readonly IReceiptReceivedHandler _handler;
    private readonly ILogger<ReceiptReceivedFunction> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ReceiptReceivedFunction(IReceiptReceivedHandler handler, ILogger<ReceiptReceivedFunction> log)
    {
        _handler = handler;
        _log = log;
    }

    [Function(nameof(ReceiptReceivedFunction))]
    public async Task Run(
        [ServiceBusTrigger("q.receipt.received", Connection = "Azure:ServiceBusConnectionString")]
        string messageJson,
        FunctionContext context,
        CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<MessageEnvelope<ReceiptReceived>>(messageJson, JsonOpts);
        if (envelope is null)
        {
            _log.LogError("Invalid message payload (cannot deserialize).");
            return;
        }

        // Handler owns tenant scoping + idempotency + db writes + audit
        await _handler.HandleAsync(envelope, ct);
    }
}
