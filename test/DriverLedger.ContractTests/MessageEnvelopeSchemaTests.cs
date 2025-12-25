using System.Text.Json;
using DriverLedger.Application.Messaging;
using FluentAssertions;
using NJsonSchema;

namespace DriverLedger.ContractTests;

public sealed class MessageEnvelopeSchemaTests
{
    [Fact]
    public async Task Envelope_matches_schema_v1()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Schemas", "message-envelope.v1.schema.json");
        var schemaJson = await File.ReadAllTextAsync(schemaPath);
        var schema = await JsonSchema.FromJsonAsync(schemaJson);

        var env = new MessageEnvelope<object>(
            MessageId: Guid.NewGuid().ToString(),
            Type: "receipt.received.v1",
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid().ToString("N"),
            Data: new { receiptId = Guid.NewGuid(), fileObjectId = Guid.NewGuid() }
        );

        var json = JsonSerializer.Serialize(env, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var errors = schema.Validate(json);

        errors.Should().BeEmpty();
    }
}
