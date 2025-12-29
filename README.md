# DriverLedger — RideShare Business Management System (Ontario)

DriverLedger is a **mobile-first business operations platform** for rideshare drivers in Ontario.  
Its goal is to transform day-to-day driving activity into **clean books, CRA-ready outputs, and real-time business insight** — continuously, not just at tax time.

This is **not** a simple expense tracker.  
DriverLedger is designed as a **Driver Business OS**:  
part bookkeeping system, part compliance assistant, part profitability dashboard.

---

## 🎯 Core Objectives

DriverLedger is built to help rideshare drivers:

- Eliminate guesswork at tax time
- Maintain **audit-ready records year-round**
- Understand **true profitability**, not just gross earnings
- Confidently file GST/HST and prepare T2125 summaries
- Respond to CRA reviews with structured evidence

The system continuously captures and reconciles:
- Income (Uber, Lyft, multi-platform)
- Expenses and receipts
- Mileage and business-use percentages
- GST/HST collected and Input Tax Credits (ITCs)

---

## 🧠 AI-Assisted, Accountant-Grade by Design

A core differentiator of DriverLedger is its **AI-assisted workflow**, designed with strict guardrails:

- AI **does not directly access the database**
- AI operates through **approved backend APIs**
- All AI-assisted actions are:
  - Confidence-scored
  - Reviewable
  - Logged in an immutable audit trail

Examples of AI assistance:
- Receipt classification and confidence scoring
- Flagging ambiguous or risky claims
- Detecting missing statements or mileage gaps
- Explaining GST/HST calculations in plain language
- Generating “live” financial summaries on demand

The user remains fully in control.

---

## 🧩 Product Scope (High-Level)

### Driver (Primary User)
- Tax profile & filing configuration (Ontario-first)
- Receipt capture and evidence vault
- Income statement ingestion (Uber/Lyft PDFs)
- Mileage tracking & business-use calculation
- Live financial statement (profit, tax exposure, readiness)
- GST/HST return guidance and exports
- Audit trail and evidence packages

### Admin (Internal / Technical Oversight)
- Platform health monitoring
- Processing pipeline visibility
- Error resolution and system diagnostics
- No involvement in driver bookkeeping or tax decisions

---

## 📊 Live Statement Concept

DriverLedger maintains a **Live Statement** for each tax year:

- Always current
- Continuously reconciled
- Clearly distinguishes:
  - Final values
  - Estimated values
  - Items pending review
- Every number is traceable to:
  - Source documents
  - User inputs
  - Explicit assumption policies

This transforms compliance from a one-time event into an ongoing process.

---

## 🎨 UI / UX Design (Source of Truth)

The product UI is being designed using **Magic Patterns → Figma**, with a strict **mobile-first** philosophy.

The published design preview can be viewed here:

🔗 **Figma Design Preview**  
https://award-power-84755408.figma.site

> Note: The frontend code in this repository is **not yet implemented**.  
> The Figma design serves as the **visual and interaction blueprint** that will later be translated into an Angular PWA.

---

## 🏗️ Planned Technical Architecture

### Frontend
- Angular (PWA, mobile-first)
- Offline-tolerant workflows
- Real-time updates via SignalR / push notifications

### Backend
- .NET (Clean Architecture)
  - Domain
  - Application
  - Infrastructure
  - API
- Azure SQL (primary data store)
- Azure Blob Storage (receipts, statements, exports)

### Background Processing
- Azure Functions
- Azure Service Bus (event-driven pipelines)
- Azure Document Intelligence (OCR & document extraction)

### AI Layer
- Controlled AI orchestration
- Tool-based access via backend APIs
- Full auditability of AI actions

---

## 📁 Repository Structure (Planned)
```
src/
DriverLedger.Domain
DriverLedger.Application
DriverLedger.Infrastructure
DriverLedger.Api
DriverLedger.Functions

tests/
DriverLedger.Domain.Tests
DriverLedger.Application.Tests
```

---

##  Core Domain Model (Current DB Schema)
The DbContext currently defines the following tenant-scoped data model (with global query filters for tenant isolation): 7

- Identity / Tenant
    - Users, Roles, UserRoles
    
    - DriverProfiles

- Files
    - FileObjects (blob metadata + hash)

- Receipts
    - Receipts
    
    - ReceiptExtractions
    
    - ReceiptReviews

- Ledger (immutable)
    - LedgerEntries
    
    - LedgerLines
    
    - LedgerSourceLinks (receipt/statement/file linkage)

- Live Statement Snapshots
    - LedgerSnapshots
    
    - SnapshotDetails

- Ops / Auditing / Notifications
    - ProcessingJobs (idempotency + job tracking)
    
    - AuditEvents (append-only audit trail)
    
    - Notifications

Ledger immutability is enforced by an EF Core interceptor that blocks updates/deletes on ledger entries/lines. 10

### API Surface (Current)
- Auth
    - POST /auth/register
    
    - POST /auth/login
    
    - GET /auth/me (authorized drivers)

- Files
    - POST /files (multipart upload, creates FileObject + audit)


- Receipts
    - GET /receipts?status=...
    
    - POST /receipts (create receipt from fileObject)
    
    - POST /receipts/{id}/submit (publishes receipt.received.v1)
    
    - POST /receipts/{id}/review/resolve (resubmit or ready-for-posting)


- Ledger
    - GET /ledger?periodType=monthly|ytd&periodKey=...
    
    - GET /ledger/{entryId}
    
    - GET /ledger/audit?sourceType=...&sourceId=...
    
    - POST /ledger/manual
    
    - POST /ledger/adjustments


- Live Statement
    - GET /live-statement?periodType=monthly|ytd&periodKey=...
    
    - GET /live-statement/timeline?periodType=monthly&year=YYYY
    
    - GET /live-statement/drilldown?metricKey=...&periodKey=...


### Message Contracts & Pipelines (Current)

Message Envelope
- All Service Bus payloads share a versioned envelope with MessageId, Type, OccurredAt, TenantId, CorrelationId, and Data. 

Receipt Pipeline
- API publishes receipt.received.v1 to q.receipt.received. 

- ReceiptReceivedFunction gate → idempotency + status transition → extraction. 

- ReceiptExtractionHandler calls Azure Document Intelligence, persists ReceiptExtractions, decides HOLD vs Ready, emits:
    
    - receipt.extracted.v1
    
    - receipt.hold.v1 or receipt.ready.v1


- ReceiptReadyFunction triggers ledger posting via ReceiptToLedgerPostingHandler. 

- ReceiptHoldFunction logs hold events + idempotent processing. 

Ledger Posting → Snapshot Pipeline
    - Ledger posting emits ledger.posted.v1 to q.ledger.posted. 
    - LedgerPostedFunction triggers SnapshotCalculator to recompute Monthly and YTD snapshots.

Schemas / Contract Tests
- JSON schemas exist for:

    - receipt.received.v1
    
    - receipt.extracted.v1
    
    - ledger.posted.v1

    - message-envelope.v1


### Observability & Security
Observability
    - Application Insights configured in API and Functions. 
    - Correlation IDs propagated by API middleware into logs and message envelopes. 
    - Structured audit trail in AuditEvents for receipt lifecycle, ledger posting, snapshot updates. 

### Security & Tenant Isolation
- JWT auth with RBAC policies: RequireDriver, RequireAdmin. 2

- Tenant isolation enforced through TenantScopeMiddleware + EF Core global query filters. 

### Test Coverage (Current)
Unit Tests
    - Receipt confidence scoring
    - HOLD evaluation rules
    - Authority score computation
    - Ledger immutability enforcement


Integration Tests
    - Receipt submit → Service Bus publish
    - Extraction + posting idempotency
    - Snapshot updates after ledger posting
    - Tenant isolation & auth protections


Contract Tests
- Envelope schema validation
- Event schema validation for receipt/ledger events


## 📌 Vision Statement

DriverLedger aims to become the **default financial operating system** for rideshare drivers in Ontario — one that prioritizes **clarity, trust, auditability, and long-term correctness** over shortcuts.

---


