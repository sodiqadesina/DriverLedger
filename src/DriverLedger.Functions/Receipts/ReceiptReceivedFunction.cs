using System.Text.Json;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Receipts;

namespace DriverLedger.Functions.Receipts;

public sealed class ReceiptReceivedFunction
{
    private readonly IReceiptReceivedHandler _gate;
    private readonly ReceiptExtractionHandler _extract;
    private readonly ILogger<ReceiptReceivedFunction> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ReceiptReceivedFunction(
        IReceiptReceivedHandler gate,
        ReceiptExtractionHandler extract,
        ILogger<ReceiptReceivedFunction> log)
    {
        _gate = gate;
        _extract = extract;
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

        // 1) Gate: idempotency + status transition + audit
        await _gate.HandleAsync(envelope, ct);

        // 2) Extraction: idempotent via ProcessingJobs jobType=receipt.extract
        await _extract.HandleAsync(envelope, ct);
    }
}
