# paprika

Local-first document-processing platform with versioned classification workflows. Upload a mixed packet, classify and extract documents, verify references, review corrections, and export approved results with source-page and workflow provenance.

Upgrading from PaperworkClassifier? See [rename and upgrade notes](docs/renaming.md) for configuration changes, existing data, request headers and sign-in.

## Run offline

Prerequisites: **.NET 10 SDK**, **Node.js 22.22.3+ in the 22.x line** (22.23.4 used here), npm, and PowerShell 7. The frontend uses Angular 22.

From this folder:

```powershell
./scripts/dev.ps1 -Install
```

Open **http://127.0.0.1:4200**, then upload PDF/PNG/JPEG files or choose **Try demo packet**. The first build can take a moment. Subsequent runs can omit `-Install`. Ctrl+C stops both processes started by the launcher.

The default **Simulation** mode processes uploads entirely locally: it extracts embedded PDF text and uses deterministic document-type and boundary rules. It makes **no Azure or Jev calls**. Images and scanned pages without embedded text are marked unknown for manual review; simulation does not fabricate OCR. The review screen and exported manifest identify simulation results explicitly. Rule scores are illustrative, not measured AI confidence.

**Try demo packet** creates a separate synthetic example with fixed predictions. Neither mode establishes Jev accuracy. Explicit **Live** mode requires configured providers and never silently falls back to simulation.

### Separate terminals

API:

```powershell
dotnet run --project server/paprika.Api.csproj --no-launch-profile --urls http://127.0.0.1:5080
```

Angular (from `client`):

```powershell
npm ci
npm start -- --host 127.0.0.1
```

The Angular development server proxies `/api` to port 5080. Local mode is the default; configured shared mode adds authenticated identities, workflow access and review claims. See `docs/operations-platform.md`.

## Run as one packaged application

The qualified platform release is `artifacts/publish-platform`. It serves Angular, the API, PDF worker, and OpenAPI from the same origin; Node and the Angular dev server aren't needed at runtime. Earlier packages are preserved.

```powershell
./artifacts/publish-platform/run-published.ps1
```

Open **http://127.0.0.1:5080**. Requires the .NET 10 ASP.NET Core runtime and PowerShell 7. Stop any development API on port 5080 first. `scripts/publish.ps1` creates a release from source; it refuses to overwrite existing output, so choose a fresh `-OutputDirectory` when rebuilding. See `docs/packaging.md`.

## Review workflow

Choose a published **classification workflow** for intake. Each workflow pins a release of reusable document types, its processing mode, splitting defaults and identifier requirements. The workflow manager supports creating, cloning, editing drafts, publishing new versions and archiving definitions. Draft sample previews use the same simulation pipeline and are marked as test runs.

Document-type splitting rules can inherit the workflow default or use classifier boundaries, separate pages, consecutive same-type pages, manual review, fixed page counts, first/last-page markers or identifier changes. Sample preview shows the effect before publication. See `docs/workflows.md` for the full setup procedure and version behavior.

1. Create a demo packet or upload PDF/PNG/JPEG files in simulation or live mode.
2. Wait for the packet to become review-ready.
3. Inspect page previews, OCR text, proposed document types, and boundary decisions.
4. Split or merge documents, move/reorder pages, relabel documents, and optionally enter a business reference.
5. Save a draft, or approve the complete packet.
6. Export a ZIP of document PDFs and a JSON manifest, or download an individual approved document PDF.

Search the inbox by packet name or business reference and filter by status/mode. Review history shows earlier saved revisions and their document assignments without replacing your current edits.

Labeled identifier candidates include page evidence. **Use reference** applies a reviewer-selected candidate. Optional reference policies match immutable datasets or configured HTTP lookup services, retain provenance and gate export on verification. Representative contracts and access remain to be supplied.

Uploads use an idempotency key so retrying a lost response returns the original submission. Cancel queued/in-flight work, or reprocess into a new linked run without replacing earlier approvals or artifacts.

After approval, queue a **simulated delivery**, or use a configured folder/HTTP connector. Simulated scenarios exercise success, transient retries and terminal rejection. Real connectors retain immutable payloads, idempotency keys and separate transport/business receipts; synthetic reference evidence blocks real delivery. See `docs/connectors-platform.md`.

The operations panel shows processing/delivery totals, durable queue counts and a bounded list of dead letters. **Replay** requests a new attempt after investigating the failure; a repeated-failure simulation will fail again. Refresh the summary to see the outcome. Replaying a message does not bypass approval or cancellation rules.

Every page must belong to exactly one document. The API rejects duplicate/missing pages and stale edits. Export requires the saved, approved revision; subsequent edits require a new approval. Predictions remain separate from reviewer corrections.

The initial General logistics release contains BOL, POD, packing list, invoice, receipt and unknown. These are **PoC definitions** to validate with business reviewers. Add custom versioned document types and bundle them in releases for other workflows.

## Platform capabilities

Open **Platform tools** to manage models, QA, references, extraction, visual templates, cases, flows and team operations. Enable capabilities in a draft workflow's structured editor, then publish. Accepted packets freeze the exact definitions and policies.

Implementation priority follows replacement value and dependencies:

1. Selective review and independent sampled QA.
2. Verified business references and immutable lookup datasets.
3. Folder intake/export and idempotent HTTP delivery.
4. Shared review claims, workflow permissions and SLA history.
5. Versioned field/table extraction and correction history.
6. Learned text classifiers and held-out evaluation gates.
7. Visual templates, PDF rasterization and image preprocessing.
8. Cross-submission cases and document checklists.
9. Revision-scoped processing flows with approval-aware output.
10. Archive/deletion controls, searchable PDFs and pixel-redacted outputs.

Manual approval is the default. Automatic approval requires an approved nonsynthetic learned model and evaluation gates; QA samples use a separate reviewer. Synthetic evaluation demonstrates behavior, not live accuracy. Extraction tables are delimiter-based, rotation is explicit, PDF text output uses Latin-1, and redaction regions are user-defined. Module guides under `docs/*-platform.md` describe configuration and limits.

## Live Azure OCR + Jev

The backend reads these configuration names (environment variables use `__`):

| Variable | Purpose |
| --- | --- |
| `paprika__Mode` | Seeds a new catalog's default mode: `Simulation` or `Live`; legacy `Demo` seeds Simulation while the demo endpoint remains separate |
| `paprika__AzureEndpoint` | HTTPS endpoint of your Azure Document Intelligence resource |
| `paprika__AzureKey` | Azure Document Intelligence key; backend only |
| `paprika__JevApiKey` | TypeSafe API key; backend only |
| `paprika__JevModel` | Model/alias available to your account; default `jev-latest` |
| `paprika__DataDirectory` | Optional local data directory for SQLite, source files, and artifacts |





```powershell
$env:paprika__Mode = 'Live'
$env:paprika__AzureEndpoint = 'https://YOUR-RESOURCE.cognitiveservices.azure.com'
```

Start Angular separately as above. Restart the API after changing configuration. The UI shows provider readiness as booleans; configuration presence does not prove successful service authentication.

For an existing catalog, publish/select a **Live workflow** using the approved model exposed by the server. Runtime mode seeds a new catalog; it does not change already published workflows or accepted packets. Ordinary retry and reprocessing retain the original workflow snapshot; choosing another workflow explicitly creates a new linked run.

Live input is sent to Azure for OCR, and OCR text/context is sent to TypeSafe for classification. Use approved samples and services. The Azure free tier processes only the first two pages; use a tier that supports your full packet. Incomplete OCR must not be mistaken for a complete packet.

### OpenAI instead of Azure/Jev

With only `OPENAI_API_KEY` (or `paprika__OpenAiApiKey`) configured, Live mode uses OpenAI for both steps:

- **Reading pages:** embedded PDF text is used as-is; scanned and image pages are transcribed by `gpt-6-luna` vision through the Responses API (`store: false`).
- **Classification:** the OpenAI [Decisions API](https://developers.openai.com/api/docs/guides/decisions) answers a document-type choice and a starts-a-new-document predicate with probabilities, like Jev.

Azure OCR and Jev still take precedence when their keys are set. Optional settings: `paprika__OpenAiModel` (classifier, default `gpt-6-luna`, the only Decisions model), `paprika__OpenAiReadModel` (default `gpt-6-luna`), `paprika__OpenAiBaseUrl`. Live workflows must use the model the server reports as `liveModel`.

```powershell
```

A synthetic scanned POD cost about 2,600 input / 190 output tokens to read and 520 input tokens to classify (about $0.0004 per scanned page at published `gpt-6-luna` rates). Live input is sent to OpenAI; use approved data.

## OpenAPI contract

The API generates an **OpenAPI 3.0** document directly from its routes and public DTOs:

**http://127.0.0.1:5080/openapi/v1.json**

Import that URL into an OpenAPI-compatible client or client generator. It covers the registered workflow, processing, review, platform and delivery routes. It includes named JSON schemas, error/status responses, binary response types, optional upload `Idempotency-Key`, and the required `X-paprika: 1` mutation header (a request marker, not authentication). The legacy `X-PaperworkClassifier: 1` header remains accepted for compatibility. Shared mode additionally requires a configured authenticated identity.

Upload uses repeated `files` fields in `multipart/form-data`. Processing returns the queued packet immediately; poll its status. Review requires the current revision and complete page membership. A `409` indicates stale state or an operation unavailable in the packet's current state. Editing an approved packet clears approval; approve the resulting revision separately.

The document is generated rather than hand-maintained. Backend tests compare it with registered routes and runtime DTOs; Playwright checks validate it with Swagger Parser, including internal schema references.

## Implementation layout

- `client/`: Angular standalone review workspace, Material controls, PDF.js page preview.
- `server/`: ASP.NET Core API, durable SQLite jobs, file storage, Azure OCR and Jev adapters, review validation and export.
- `server.Tests/`: backend regression tests.
- `tests/`: Playwright API/browser workflow tests.
- `docs/evaluation.md`: real-paperwork evaluation procedure.
- `docs/workflows.md`: reusable document definitions, releases, workflow configuration and draft previews.
- `scripts/dev.ps1`: local two-process launcher.

Processing runs locally in one backend process. Source files and state default to `%LOCALAPPDATA%/paprika`, outside the source tree. An existing legacy database is reused when the new default has no database; an explicit `paprika__DataDirectory` takes precedence. See [data-directory selection](docs/renaming.md#existing-data). Keep high-churn execution data outside OneDrive. Retention supports explicit archival and dependency-checked physical deletion; inspect the preview before requesting deletion.

**WolverineFx** uses SQLite-backed durable messaging for processing and delivery. Application state and message handoff share a transaction; separate queues control concurrency and preserve pending retries across restarts. No external broker is required. See `docs/wolverine.md` for transaction, migration, retry and replay details.

In Live mode, Azure Document Intelligence supplies OCR using API version `2024-11-30`. Jev supplies typed document classification and an independent page-boundary decision. Simulation extracts embedded PDF text locally and uses separate deterministic type/boundary rules. Matching page types alone do not establish that pages belong to the same document. Confidence or rule scores are shown for review, not presented as measured accuracy rates. Exports require an approved exact revision and its configured release checks.

## Checks

**CSharpier is the project's C# formatter**, pinned as a local .NET tool. From the root:

```powershell
dotnet tool restore
dotnet csharpier format .
dotnet csharpier check .
```

`./scripts/verify.ps1` runs the formatting check, backend tests, offline evaluation tests (Python 3), and Angular production build. Add `-Browser` to run Playwright against already-running servers. In your editor, use the CSharpier extension with format-on-save.

Backend:

```powershell
dotnet test server.Tests
```

Frontend production build (from `client`):

```powershell
npm run build
```

Browser/API smoke tests with both servers running (from `tests`):

```powershell
npm ci
npx playwright install chromium
npm test
```

These tests create synthetic packets in the running API's data directory. Use an isolated `paprika__DataDirectory` for test runs if desired. Synthetic and mocked-provider checks do not replace a live end-to-end service test and held-out real-paperwork evaluation.

The platform release passed **226 backend tests**, **14 evaluator/recovery-evidence tests**, and **30 packaged browser/API tests**, plus formatting, Angular build, prior-workflow migration, forced-stop recovery and the 10-case synthetic benchmark. Details and limits: `docs/platform-verification.md`. The previous workflow release evidence remains in `docs/completion.md`.

Run `./scripts/acceptance.ps1` to start the packaged application in simulation mode with isolated synthetic test inputs, execute browser/API checks, and stop it automatically. Port 5080 must be free. It does not contact live providers or alter your regular packet database.

Run `./scripts/recovery-check.ps1 -SeedPackageDirectory ./artifacts/publish-durable` to check the prior-release upgrade, and `./scripts/durability-check.ps1` for forced-stop pending-message recovery. Both use disposable synthetic data and clean up their own processes. Repeated transient-failure scenarios deliberately produce error-level logs during these checks.

Run `./scripts/benchmark.ps1` for the isolated **10-case synthetic benchmark**. Its 24 pages yielded 23 correctly typed pages, 14/18 exact ordered document reconstructions and 13/18 fully correct documents. Manual correction produced matching approved manifests for all 18 documents. The workflow release preserved these baseline results. These are offline heuristic results, not real-document or live-provider accuracy. Results and limitations: `docs/benchmark.md` and `artifacts/benchmark-workflows-report.json`.

Dependency audit: `npm audit` reports no vulnerabilities in the frontend's production or development dependencies. Angular 22 supports the installed Node 22.23.4 baseline and removes the earlier Angular 21 CLI advisories.

## PoC boundaries


See `docs/requirements.md` for the requirement-to-evidence map and inputs needed for production completion, and `docs/completion.md` for verified release evidence.

Provider references: [Jev API](https://docs.typesafe.ai/api/system-one.md), [Azure Document Intelligence](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/).
