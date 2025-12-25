using System.Text.Json;
using DriverLedger.Application.Messaging;
using FluentAssertions;

namespace DriverLedger.UnitTests;

public sealed class MessageEnvelopeTests
{
    [Fact]
    public void MessageEnvelope_serializes_with_expected_properties()
    {
        var env = new MessageEnvelope<object>(
            MessageId: "mid",
            Type: "receipt.received.v1",
            OccurredAt: DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            TenantId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CorrelationId: "cid",
            Data: new { receiptId = "rid", fileObjectId = "fid" }
        );

        var json = JsonSerializer.Serialize(env, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"messageId\"");
        json.Should().Contain("\"type\"");
        json.Should().Contain("\"occurredAt\"");
        json.Should().Contain("\"tenantId\"");
        json.Should().Contain("\"correlationId\"");
        json.Should().Contain("\"data\"");
    }
}
