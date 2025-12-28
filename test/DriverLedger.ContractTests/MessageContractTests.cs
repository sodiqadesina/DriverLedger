using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Application.Receipts.Messages;
using FluentAssertions;
using NJsonSchema;
using System.Text.Json;

namespace DriverLedger.ContractTests
{
    public sealed class MessageContractTests
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task receipt_received_v1_payload_matches_schema()
        {
            var payload = new ReceiptReceived(
                ReceiptId: Guid.NewGuid(),
                FileObjectId: Guid.NewGuid()
            );

            var envelope = NewEnvelope("receipt.received.v1", payload);

            await AssertEnvelopeDataMatchesSchema(envelope, "Schemas/receipt.received.v1.json");
        }

        [Fact]
        public async Task receipt_extracted_v1_payload_matches_schema()
        {
            var payload = new ReceiptExtractedV1(
                ReceiptId: Guid.NewGuid(),
                FileObjectId: Guid.NewGuid(),
                Confidence: 0.85m,
                IsHold: false,
                HoldReason: ""
            );

            var envelope = NewEnvelope("receipt.extracted.v1", payload);

            await AssertEnvelopeDataMatchesSchema(envelope, "Schemas/receipt.extracted.v1.json");
        }

        [Fact]
        public async Task ledger_posted_v1_payload_matches_schema()
        {
            var payload = new LedgerPosted(
                LedgerEntryId: Guid.NewGuid(),
                SourceType: "Receipt",
                SourceId: Guid.NewGuid().ToString("D"),
                EntryDate: "2025-01-10"
            );

            var envelope = NewEnvelope("ledger.posted.v1", payload);

            await AssertEnvelopeDataMatchesSchema(envelope, "Schemas/ledger.posted.v1.json");
        }

        private static MessageEnvelope<T> NewEnvelope<T>(string type, T data) =>
            new(
                MessageId: Guid.NewGuid().ToString("N"),
                Type: type,
                OccurredAt: DateTimeOffset.UtcNow,
                TenantId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid().ToString("N"),
                Data: data
            );

        private static async Task AssertEnvelopeDataMatchesSchema<T>(MessageEnvelope<T> envelope, string schemaPath)
        {
            // Serialize the FULL envelope as it would be published
            var json = JsonSerializer.Serialize(envelope, JsonOpts);

            using var doc = JsonDocument.Parse(json);

            // Ensure envelope shape includes Data
            doc.RootElement.TryGetProperty("data", out _).Should().BeTrue("MessageEnvelope<T> must contain 'data'");

            // Extract the payload node only and validate it against schema
            var dataNode = doc.RootElement.GetProperty("data").GetRawText();

            var schema = await JsonSchema.FromFileAsync(schemaPath);
            var errors = schema.Validate(dataNode);

            errors.Should().BeEmpty($"Schema validation failed for {schemaPath}: {string.Join(", ", errors.Select(e => e.ToString()))}");
        }
    }
}
