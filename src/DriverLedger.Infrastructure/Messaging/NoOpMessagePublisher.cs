// this is for test

using DriverLedger.Application.Messaging;

namespace DriverLedger.Infrastructure.Messaging;

public sealed class NoOpMessagePublisher : IMessagePublisher
{
    public Task PublishAsync<T>(
        string entityName,
        MessageEnvelope<T> envelope,
        CancellationToken ct)
        => Task.CompletedTask;
}
