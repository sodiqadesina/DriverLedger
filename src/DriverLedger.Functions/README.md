# DriverLedger.Functions

## Purpose
The Functions project hosts Azure Functions that process Service Bus events for receipt extraction, ledger posting, and Live Statement snapshot updates.

## Responsibilities
- Consume `receipt.received.v1` and trigger OCR extraction.
- Consume `receipt.extracted.v1` and post immutable ledger entries.
- Consume `ledger.posted.v1` and recompute Live Statement snapshots.
- Provide telemetry and structured logging via Application Insights.

## Function Entry Points
- **ReceiptReceivedFunction**: Idempotency gate + extraction pipeline.
- **ReceiptExtractedFunction**: Ledger posting pipeline.
- **LedgerPostedFunction**: Snapshot recalculation pipeline.

## How It Connects to the System
- The API publishes Service Bus messages.
- Functions deserialize standardized envelopes from the Application layer.
- Infrastructure handlers perform database updates, auditing, and downstream event publishing.
- Results are surfaced to drivers through API endpoints.

## Testing
- Integration tests validate that receipt submission triggers downstream processing.
- Idempotency tests confirm repeated messages do not double-post ledger entries.
