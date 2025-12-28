# DriverLedger.Api

## Purpose
The API project is the public HTTP surface for DriverLedger. It handles authentication, request validation, tenant scoping, and publishes events that drive the asynchronous processing pipeline.

## Responsibilities
- Expose REST endpoints for drivers to upload files, submit receipts, and read ledger and Live Statement data.
- Enforce authentication and role-based authorization.
- Establish tenant context and correlation IDs for every request.
- Publish domain events to Azure Service Bus.

## Key Modules
- **Auth**: User registration, login, and identity checks.
- **Files**: Receipt file upload and metadata persistence.
- **Receipts**: Receipt creation, submission, status query, and review resolution.
- **Ledger**: Read-only ledger list and entry detail endpoints.
- **Live Statement**: Snapshot retrieval, timeline, and drilldown APIs.

## How It Connects to the System
1. A driver uploads a file through the API, which stores the binary in Blob Storage and persists `FileObject` metadata in SQL.
2. A receipt is created and submitted; the API publishes `receipt.received.v1` to Service Bus.
3. Azure Functions consume the message to perform OCR extraction and ledger posting.
4. `ledger.posted.v1` triggers snapshot recalculation; the API reads snapshots to render Live Statement views.

## Observability and Security
- Correlation IDs are generated or propagated per request and included in all downstream events.
- Tenant scoping is enforced through middleware and global query filters.
- JWT-based authentication and role policies gate access to endpoints.

## Testing
- Integration tests validate request flows (submit receipt, auth, tenant isolation) and ensure message publication and snapshot updates are wired correctly.
- Contract tests ensure API-emitted messages conform to schema expectations for downstream consumers.
