# DriverLedger.Application

## Purpose
The Application project defines stable contracts and abstractions shared across the API, Infrastructure, and Functions layers. It provides message envelopes, event contracts, and service interfaces so layers remain decoupled.

## Responsibilities
- Define Service Bus message envelopes and versioned event payloads.
- Provide interfaces for request context, tenant resolution, and auditing.
- Establish extractor interfaces for receipt OCR implementations.

## Key Components
- **Messaging**: `MessageEnvelope<T>` and versioned event records for receipt and ledger pipelines.
- **Common**: `IRequestContext`, `ITenantProvider`, `IClock` abstractions.
- **Receipts**: Message payload contracts and extractor interface.
- **Auditing**: `IAuditWriter` interface.

## How It Connects to the System
- The API publishes events using these contracts.
- Azure Functions deserialize and process the same contracts.
- Infrastructure implements the interfaces for persistence and external integrations.

## Testing
- Contract tests validate event schemas derived from these models.
- Integration tests validate message shapes end-to-end via the API and Functions pipeline.
