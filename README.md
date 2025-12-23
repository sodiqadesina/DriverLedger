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

## 🚧 Project Status

- Architecture and product scope defined
- UI/UX design in progress (Figma)
- Backend scaffolding in progress
- This repository will evolve incrementally with:
  - Domain modeling
  - Event pipelines
  - Tax calculation engines
  - Frontend implementation

---

## 📌 Vision Statement

DriverLedger aims to become the **default financial operating system** for rideshare drivers in Ontario — one that prioritizes **clarity, trust, auditability, and long-term correctness** over shortcuts.

---


