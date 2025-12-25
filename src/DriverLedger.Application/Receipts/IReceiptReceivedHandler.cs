using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
namespace DriverLedger.Application.Receipts;

public interface IReceiptReceivedHandler
{
    Task HandleAsync(
        MessageEnvelope<ReceiptReceived> envelope,
        CancellationToken ct);
}
