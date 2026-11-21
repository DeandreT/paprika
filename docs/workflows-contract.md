# Workflow implementation contract

Shared implementation contract for the approved multiple-workflow release. JSON uses camelCase. Draft definitions are validated on save and publication. Version references are exact.

## Catalog

`GET /api/workflows` includes `classifierProviders: { id: string, model: string, configured: boolean }[]` alongside `entries`, `defaultWorkflow`, `policies`, `providerProfiles`, `liveModel`, and `stats`. Use the selected classifier's `model` and `configured` fields for live workflow configuration. `liveModel` is retained for older clients. Simulation uses `local-simulation-1`.

`canUpload` describes the current caller's permission. In shared mode a non-administrator sees only classifier rows used by the published workflows they can read. A live legacy entry may include `resolvedClassifierProvider`; if it is absent on an entry with profile `default` or `null`, the UI must not guess from the visible rows. An ambiguous or obsolete workflow can be copied into a draft for repair. Select a valid classifier and save that draft before publishing it.

- `VersionRef`: `{ id: string, version: number }`
- `SplitPolicy`: `{ mode: 'inherit'|'classifier'|'separate'|'consecutive'|'manual'|'fixed'|'first'|'last'|'identifier', pageCount: number, pattern: string }`. Type definitions may inherit; workflow defaults must be explicit. Fixed uses pageCount; marker modes use a bounded .NET regex; identifier uses the first captured group. Source boundaries always split. Missing/conflicting evidence yields review reasons. Unknown pages remain preserved.
- `CatalogEntry`: `{ kind: 'type'|'release'|'workflow', id, version, revision, status: 'draft'|'published'|'archived', name, description, documentType?: TypeConfig, release?: ReleaseConfig, workflow?: WorkflowConfig }`.
- `TypeConfig`: `{ keywords: string[], instructions: string, split: SplitPolicy }`. The entry ID is the stable classification type ID.
- `ReleaseConfig`: `{ types: VersionRef[] }`. Must include the `unknown` type.
- `WorkflowConfig`: `{ release: VersionRef, mode: 'simulation'|'live', providerProfile: 'default'|'jev'|'openai', model: string, instructions: string, defaultSplit: SplitPolicy, identifierKinds: string[], requiredIdentifierKinds: string[] }`. Use `default` for simulation and `jev` or `openai` for an explicit live classifier. Older live configurations with `default` are resolved against their model. Supported identifier kinds: load, shipment, order, bol, receipt. Every run requires manual approval; required identifiers add review reasons.
- `WorkflowStats`: `{ id, version, packets: number, review: number, failed: number, approved: number, corrections: number }` (test runs excluded).

Mutations use the `X-paprika: 1` header and return the saved `CatalogEntry`, except setting the default returns `VersionRef`:

- `POST /api/workflows/catalog`: create draft; body `{ kind, id?, name, description, documentType?, release?, workflow? }`.
- `PUT /api/workflows/catalog/{kind}/{id}/{version}`: edit draft; body is full entry including expected `revision`.
- `POST /api/workflows/catalog/{kind}/{id}/{version}/publish`: `{ revision }`.
- `POST /api/workflows/catalog/{kind}/{id}/{version}/archive`: `{ revision }`.
- `POST /api/workflows/catalog/{kind}/{id}/{version}/versions`: `{ revision }`; create new draft version copied from this entry.
- `POST /api/workflows/catalog/{kind}/{id}/{version}/clone`: `{ revision, name }`; create a new identity/draft version 1.
- `PUT /api/workflows/default`: `{ id, version }`; published workflow required.

Errors use `{ error: string }` and 400/404/409. Publish resolves and validates referenced published type/release versions. Archived versions remain available for historical lookup. Archiving the current default is rejected until another default is selected.

## Intake and runs

`POST /api/packets` retains multipart `files` and adds optional `workflowId`, `workflowVersion`, `testRun` (`true` for a draft preview), and `workflowRevision` (required for draft preview). Omitting workflow selects the current default; idempotent replay resolves the original selection before default/readiness changes. An explicitly changed selector with the same key conflicts. Registry resolution, non-secret snapshot and packet/message acceptance commit atomically.

Preview accepts an exact saved draft workflow revision; referenced release/type versions may be drafts and are frozen into the snapshot. Preview is simulation-only in this release, clearly marked `testRun: true`, and uses the same classification/splitting implementation as published simulation. It supports review and preview/export with explicit test provenance; simulated delivery rejects test runs. Publishing uses only published dependencies. An unsaved editor must be saved before preview.

`POST /api/packets/{id}/reprocess` accepts `{ revision, workflowId?, workflowVersion? }`. Omission retains the parent snapshot; explicit selection starts a new linked run with that published workflow. A legacy parent retains legacy behavior. Retries/replay never re-resolve the registry.

`PacketResponse` adds `workflow: WorkflowSnapshot` and `testRun: boolean`. Legacy packets expose a deterministic legacy snapshot without rewriting history or artifacts. `WorkflowSnapshot` contains `{ id, name, version, releaseId, releaseVersion, hash, mode, providerProfile, model, instructions, defaultSplit, identifierKinds, requiredIdentifierKinds, types: SnapshotType[] }`; `SnapshotType` is `{ id, version, name, description, keywords, instructions, split }`.

Packet/history context, newly produced manifests and delivery artifacts expose workflow identity/version/hash. Existing artifact bytes remain unchanged. `/api/config.classifierConfigured` reports whether **any** classifier credential is configured; that alone is not sufficient to admit a packet for a specific workflow. No credentials appear in catalog/snapshot responses.
