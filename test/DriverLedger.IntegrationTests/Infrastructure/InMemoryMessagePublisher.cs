

using DriverLedger.Application.Messaging;
using DriverLedger.Infrastructure.Messaging;

namespace DriverLedger.IntegrationTests.Infrastructure
{
    public sealed class InMemoryMessagePublisher : IMessagePublisher
    {
        private readonly List<PublishedMessage> _messages = new();
        public IReadOnlyList<PublishedMessage> Messages => _messages;

        public Task PublishAsync<T>(string entityName, MessageEnvelope<T> envelope, CancellationToken ct)
        {
            _messages.Add(new PublishedMessage(entityName, envelope.Type, envelope.CorrelationId, envelope.TenantId, envelope.MessageId, envelope!));
            return Task.CompletedTask;
        }

        public sealed record PublishedMessage(
            string EntityName,
            string Type,
            string CorrelationId,
            Guid TenantId,
            string MessageId,
            object Envelope
        );
    }
}
