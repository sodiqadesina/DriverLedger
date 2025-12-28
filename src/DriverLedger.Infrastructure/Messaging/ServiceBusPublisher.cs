using Azure.Messaging.ServiceBus;
using DriverLedger.Application.Messaging;
using System.Text.Json;


namespace DriverLedger.Infrastructure.Messaging
{
    public interface IMessagePublisher
    {
        Task PublishAsync<T>(string entityName, MessageEnvelope<T> envelope, CancellationToken ct);
    }

    public sealed class ServiceBusPublisher : IMessagePublisher
    {
        private readonly ServiceBusClient _client;
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public ServiceBusPublisher(ServiceBusClient client) => _client = client;

        public async Task PublishAsync<T>(string entityName, MessageEnvelope<T> envelope, CancellationToken ct)
        {
            var sender = _client.CreateSender(entityName);

            var json = JsonSerializer.Serialize(envelope, JsonOpts);
            var msg = new ServiceBusMessage(json)
            {
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId,
                ContentType = "application/json"
            };

            msg.ApplicationProperties["type"] = envelope.Type;
            msg.ApplicationProperties["tenantId"] = envelope.TenantId.ToString();

            await sender.SendMessageAsync(msg, ct);
        }
    }
}
