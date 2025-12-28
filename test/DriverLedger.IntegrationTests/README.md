# DriverLedger.IntegrationTests

## Purpose
This project exercises full-stack behaviors across the API, database, and background handlers to validate real system interactions.

## Coverage Highlights
- Receipt submission publishes Service Bus messages.
- Extraction and ledger posting idempotency.
- Snapshot recalculation after ledger postings.
- Tenant isolation and authorization protections.
- Audit event persistence.

## How It Connects
Integration tests validate that the end-to-end pipeline works correctly when real persistence and handlers are involved.
