
using DriverLedger.Application.Messaging;
using FluentAssertions;
using System.Text.Json;

namespace DriverLedger.ContractTests
{
    public sealed class EventSchemaContractTests
    {
        // map: event type -> payload schema file name
        private static readonly Dictionary<string, string> TypeToSchema = new(StringComparer.Ordinal)
        {
            ["receipt.received.v1"] = "receipt.received.v1.schema.json",
            ["receipt.extracted.v1"] = "receipt.extracted.v1.schema.json",
            ["ledger.posted.v1"] = "ledger.posted.v1.schema.json",
        };

        [Theory]
        [MemberData(nameof(EventSamples))]
        public async Task Envelope_and_payload_match_schema(string type, object payload)
        {
            // 1) Load envelope schema
            var envelopeSchema = await LoadSchemaAsync("message-envelope.v1.schema.json");

            // 2) Build a sample envelope with the given type/payload (NOTE: generic)
            var env = new MessageEnvelope<object>(
                MessageId: Guid.NewGuid().ToString(),
                Type: type,
                OccurredAt: DateTimeOffset.UtcNow,
                TenantId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid().ToString("N"),
                Data: payload
            );

            var json = JsonSerializer.Serialize(env, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            // 3) Validate envelope schema
            var envelopeErrors = envelopeSchema.Validate(json);
            envelopeErrors.Should().BeEmpty("message envelope must match schema");

            // 4) Validate payload schema chosen by type
            TypeToSchema.TryGetValue(type, out var payloadSchemaFile)
                .Should().BeTrue($"schema mapping must exist for type '{type}'");

            var payloadSchema = await LoadSchemaAsync(payloadSchemaFile!);

            using var doc = JsonDocument.Parse(json);
            var dataJson = doc.RootElement.GetProperty("data").GetRawText();

            var payloadErrors = payloadSchema.Validate(dataJson);
            payloadErrors.Should().BeEmpty($"payload for '{type}' must match its schema");
        }

        public static IEnumerable<object[]> EventSamples()
        {
            yield return
            [
                "receipt.received.v1",
            new
            {
                receiptId = Guid.NewGuid(),
                fileObjectId = Guid.NewGuid()
            }
            ];

            yield return
            [
                "receipt.extracted.v1",
            new
            {
                receiptId = Guid.NewGuid(),
                fileObjectId = Guid.NewGuid(),
                confidence = 0.92m,
                isHold = false,
                holdReason = ""
            }
            ];

            yield return
            [
                "ledger.posted.v1",
            new
            {
                ledgerEntryId = Guid.NewGuid(),
                sourceType = "Receipt",
                sourceId = Guid.NewGuid().ToString(),
                entryDate = DateTime.UtcNow.ToString("yyyy-MM-dd") // keep schema simple for now
            }
            ];
        }

        private static async Task<NJsonSchema.JsonSchema> LoadSchemaAsync(string schemaFileName)
        {
            var schemaPath = Path.Combine(AppContext.BaseDirectory, "Schemas", schemaFileName);
            File.Exists(schemaPath).Should().BeTrue($"schema file must exist: {schemaPath}");

            var schemaJson = await File.ReadAllTextAsync(schemaPath);
            return await NJsonSchema.JsonSchema.FromJsonAsync(schemaJson);
        }
    }
}
