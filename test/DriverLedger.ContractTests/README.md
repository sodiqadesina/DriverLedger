# DriverLedger.ContractTests

## Purpose
This project verifies that published Service Bus messages conform to versioned JSON schemas and remain compatible across producers and consumers.

## Coverage Highlights
- Message envelope schema validation.
- `receipt.received.v1`, `receipt.extracted.v1`, and `ledger.posted.v1` event schemas.

## How It Connects
The API publishes these events and Azure Functions consume them. Contract tests ensure changes remain backwards compatible and safe to deploy.
