# DriverLedger.Domain

## Purpose
The Domain project is the source of truth for DriverLedger’s business entities. It models receipts, ledger entries, snapshots, audit events, and tenant-scoped identity objects.

## Responsibilities
- Define tenant-scoped domain entities and relationships.
- Represent immutable ledger concepts and append-only audit trails.
- Provide core data structures used by EF Core mappings and business rules.

## Key Entities
- **Receipts**: `Receipt`, `ReceiptExtraction`, `ReceiptReview`.
- **Ledger**: `LedgerEntry`, `LedgerLine`, `LedgerSourceLink`.
- **Statements**: `LedgerSnapshot`, `SnapshotDetail`.
- **Auditing & Ops**: `AuditEvent`, `ProcessingJob`.
- **Identity & Driver**: `User`, `Role`, `UserRole`, `DriverProfile`.
- **Files & Notifications**: `FileObject`, `Notification`.

## How It Connects to the System
- Infrastructure uses these entities for EF Core mappings and persistence.
- The API and Functions layers rely on these entities to enforce business invariants.
- Ledger entities are protected by immutability checks in Infrastructure.

## Testing
- Unit tests validate domain-driven rules such as ledger immutability and authority score behavior.
- Integration tests verify domain entities function correctly when persisted through EF Core.
