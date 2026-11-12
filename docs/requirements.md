# Completion criteria and outstanding inputs

Use the following checks to distinguish working local behavior from the evidence needed for a particular deployment.

## Local requirements and evidence

| Requirement | Implementation / repeatable evidence |
| --- | --- |
| Upload actual PDF/image packets and preserve originals | `server/Program.cs`, `Store.cs`; upload workflow and simulation tests |
| Propose types and independent document boundaries | `Simulation.cs`, `Providers.cs`, `Worker.cs`; initial prediction snapshots and evaluator |
| Leave uncertain/scanned content for review | Simulation unknown/no-OCR handling; browser image-upload check |
| Preview and correct split/merge/order/type, including manual reassembly | Angular workspace; workflow browser tests and synthetic benchmark |
| Evidence-based manual business grouping | `Identifiers.cs`, review business references and export `businessGroups`; lifecycle browser test |
| Preserve source hashes, predictions, approval history and versioned exports | `Store.cs`, `Pdfs.cs`; immutable review/reprocessing and recovery checks |
| Durable acceptance, cancellation, restart and delivery work | Wolverine SQLite transactions/generations; transaction rollback, crash, duplicate and cancellation tests |
| Automatic transient retries, dead-letter inspection/replay | `Messaging.cs`, `MessagingOperations.cs`, operations UI; fault-write and replay regressions |
| Organized PDFs/manifest and traceable downstream handoff | Approved exports plus local delivery simulator and explicit receipts; browser and recovery checks |
| Angular, CSharpier and validated OpenAPI | `scripts/verify.ps1`, OpenAPI backend tests and Swagger Parser browser test |
| Repeatable local packaging and recovery | `scripts/publish.ps1`, `acceptance.ps1`, `recovery-check.ps1`, `durability-check.ps1` |
| Measured synthetic regression behavior | `scripts/evaluate.py`, `scripts/benchmark.ps1`, `docs/benchmark.md` |
| Multiple versioned classification workflows | Workflow registry and pinned snapshots, Angular library/release/workflow forms, per-type splitting and sample previews; `tests/workflows.spec.ts`, `tests/workflow-ui.spec.ts`, workflow backend tests and recovery checks |
| Selective review, independent QA and learned classification | Quality/training stores, frozen artifacts, held-out evaluation and corrected-example deduplication; quality integration tests |
| Verified references and configured real connectors | Immutable datasets, pinned HTTP lookup bindings, folder intake/export, HTTP idempotency and business receipts; reference/connector integration tests |
| Shared review and lifecycle controls | Configured identities, workflow permissions, claims/leases, deadline history and dependency-checked retention; operations API tests |
| Structured extraction and enhanced outputs | Versioned field/delimited-table definitions, evidence/correction history, coverage gating, searchable and pixel-redacted PDF verification |
| Visual processing and immutable derivatives | PDF rasterization, image templates and explicit rotation/contrast/grayscale; native imaging tests |
| Cases and composable processing flows | Cross-submission membership/history, conditional blocks, durable revision-scoped approval continuation and retry-safe output; flow pipeline/API tests |

Platform run results are recorded in `docs/platform-verification.md`; `docs/completion.md` retains the previous workflow release evidence. Synthetic workflow and scoring checks establish local behavior, not accuracy or production acceptance.

## What prevents full production completion

1. **Workflow definition:** Owner/customer scope, approved taxonomy (including signed BOL/POD variants), required extraction, intake channels and existing processing responsibilities. These determine the actual replacement contract; a guessed definition could silently omit customer work.
2. **Quality acceptance:** Independently labeled, authorized real samples and agreed error/review thresholds. Synthetic data cannot establish suitability on real scans or validate live provider quality.
3. **Service and downstream access:** Approved OCR/Jev configuration and consuming-system contracts/test access. Transport simulation cannot establish real authentication, field mapping, business acknowledgment or idempotency behavior.
4. **Deployment decisions:** Approved hosting/database/identity environment, roles/customer boundaries, retention and recovery requirements, volumes and operational ownership. These determine shared authorization, storage and deployment configuration.
5. **Pilot/cutover authorization:** Shadow-running and reconciliation criteria, fallback ownership, and approval to deploy or retire the old workflow. Local development authorization does not supply production cutover authority.

Once those inputs are supplied, remaining work is customer-specific integration/configuration, deployment of the shared access controls, live evaluation, pilot reconciliation and cutover verification. Current field/table extraction, visual matching and PDF output have the documented limits in `docs/extraction-platform.md`.

## Sensible defaults already chosen

Use local SQLite/Wolverine, loopback hosting, a small versioned example taxonomy, default manual approval, reviewed references, explicit simulated classification/delivery and non-destructive migration. Optional shared identities, reference services, real connectors and gated automation require explicit configuration. These defaults keep the local path usable without credentials.
