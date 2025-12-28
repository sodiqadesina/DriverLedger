# DriverLedger.Infrastructure

## Purpose
The Infrastructure project implements persistence, integrations, and background handlers. It connects the Domain model to Azure SQL, Service Bus, Blob Storage, and OCR services.

## Responsibilities
- EF Core `DbContext`, entity configuration, migrations, and interceptors.
- Service Bus publishing and message metadata propagation.
- Blob Storage access for receipt files.
- Receipt extraction, confidence scoring, and HOLD evaluation.
- Ledger posting and snapshot recalculation.
- Audit event persistence and idempotency tracking.

## Key Components
- **Persistence**: `DriverLedgerDbContext`, migrations, `LedgerImmutabilityInterceptor`.
- **Messaging**: `ServiceBusPublisher` and message metadata.
- **Receipts**: Extraction handlers, confidence calculator, HOLD evaluator.
- **Ledger**: Receipt-to-ledger posting and source link creation.
- **Statements**: Snapshot calculator and authority score computation.
- **Auditing**: `AuditWriter` implementation.

## How It Connects to the System
- The API and Functions depend on Infrastructure services for data access and event publishing.
- Azure Functions use Infrastructure handlers to execute the asynchronous pipeline:
  - receipt.received → extraction → receipt.extracted → ledger posting → ledger.posted → snapshot recalculation.

## Observability and Idempotency
- Handlers include correlation IDs and tenant scoping in logs and audit events.
- `ProcessingJob` entries enforce idempotent processing across retries.
- Ledger immutability is enforced via EF Core interceptors.

## Testing
- Unit tests validate confidence scoring, HOLD logic, and authority score calculations.
- Integration tests validate idempotency and snapshot updates across the pipeline.
- Contract tests validate message schema compatibility.
