# Business references and connected intake/delivery

This module implements actual local-dataset lookups, configured HTTP lookups, explicit folder import, atomic folder export, and actual HTTP ZIP delivery. The original delivery simulator remains separate. These generic adapters are **not a downstream system contract** and have not been validated against a business service.

## Integration

- Register `AddReferencePlatform()` and `AddConnectorPlatform()`; map `MapReferencePlatform()` and `MapConnectorPlatform()`.
- Call `ReferenceService.ProcessAsync(packet, cancellationToken)` after document assembly and identifier extraction, before the processing packet is saved.
- Call static `ReferenceService.OnReview(connection, transaction, before, after, actor)` inside the review transaction after revision changes and before committing. It carries verification only when workflow hash, document ID/type, ordered pages and business reference are identical. Changed documents require a fresh lookup/verification.
- `ReferencePolicies.Validate(policy)` validates shape. `Validate(connection, transaction, policy)` additionally checks the exact immutable local dataset version/hash. `Freeze(connection, transaction, policy)` returns a detached policy or throws `WorkflowException`. These helpers use the supplied transaction; `Initialize(connection, transaction?)` is available for startup.
- Reference and connector retention participants declare their tables. Actual delivery jobs/external copies and connector intake idempotency keys block privacy purge pending reconciliation.
- `ReferencePanelComponent` is standalone (`app-reference-panel`); accepts `packetId` and `revision`, emits `refreshed`. An empty packet ID still supports global datasets/connectors.

## Immutable local reference data

`POST /api/references/datasets` takes `{ id, name, synthetic, entries: [{ reference, recordId, label }] }`. It always appends a new immutable version. There is no edit/delete endpoint. At most 10,000 bounded records are accepted. Duplicate references remain ambiguous rather than silently choosing a record. All dataset versions are returned by `GET /api/references/datasets`.

Pin the exact returned ID/version/hash in `capabilities.reference`:

```json
{
  "mode": "local",
  "datasetId": "example-orders",
  "datasetVersion": 1,
  "datasetHash": "<returned SHA-256>",
  "requireVerified": true,
  "autoVerify": false,
  "identifierKinds": ["order"]
}
```

Alternatively, use `{ "mode": "http", "connectorId": "approved-lookup", "requireVerified": true }`. The HTTP endpoint and credential are server-side configuration, never workflow fields.

Startup registers public HTTP lookup profile hashes in `reference_connector_bindings`. Workflow publication/intake freezes the approved `connectorBindingHash` into the reference policy. Credentials are excluded from the hash and never enter the registry. A lookup rejects a changed live destination rather than following a rebound connector ID. Restart after an authorized profile configuration change refreshes the registry; existing snapshots remain pinned and fail closed against the changed profile. Credential rotation alone does not change the public profile hash.

Results bind packet ID, workflow hash, exact revision, document ID/reference, dataset hash, provenance and result hash. Statuses are `candidate`, `verified`, `notFound`, `ambiguous`, and `unavailable`. Reference matching is exact (local matching is case-insensitive), not fuzzy. Multiple extracted identifier candidates stay ambiguous. A single document-local identifier candidate is assigned as the business reference during initial processing only when lookup returns exactly one record. Existing reviewed references are not silently replaced. Manual lookup results can suggest a reference, but saving a changed business reference still goes through review.

The initial inferred business reference and its lookup evidence commit together against the authoritative processing packet. Document evidence additionally binds a hash of type and ordered page IDs. `RestoreDerivedReferences(packet)` can recover only matching same-revision/workflow/document evidence after stable reassembly, so a completed flow block remains resumable without losing its inferred association. Different revisions or document boundaries are never rehydrated from old evidence.

By default, a person selects the exact returned record to verify. Explicit `autoVerify: true` permits exact, unique, non-synthetic matches to become verified automatically. Synthetic datasets are always identified and are never automatically verified; a manual synthetic verification establishes a match only within that example dataset, not operational business acceptance.

Manually reviewed synthetic evidence can support local review exports with explicit `platform.json` provenance. Actual connector delivery rejects current synthetic reference evidence, including manually verified examples. Actual connector ZIPs additionally carry `reference-provenance.json`; transport receipts retain an explicit synthetic-evidence flag (false for authorized operational delivery). Queueing, retry and recovery all recheck current reference verification and the pinned HTTP profile before sending.

`POST /api/references/packets/{packetId}/lookup` takes `{ packetId, revision, documentId? }`. `POST .../verify` takes `{ packetId, revision, documentId, resultHash, recordId }`. The route is authoritative. Human mutations require workflow review permission and, in shared mode, the current review claim checked again inside the mutation transaction. `GET /api/references/packets/{packetId}` returns retained revision results. Global dataset management is administrative.

## Server configuration

Configure `paprika:Connectors:<connector-id>` using the server configuration provider. Configure credentials through approved process-environment/secret tooling; do not put them in a workflow, browser form, repository or investigation notes. Example **non-secret** settings:

```json
{
  "paprika": {
    "Connectors": {
      "approved-inbox": { "Kind": "folderIntake", "Root": "C:/approved-paperwork/inbox" },
      "approved-outbox": { "Kind": "folderExport", "Root": "C:/approved-paperwork/outbox" },
      "approved-lookup": { "Kind": "httpLookup", "Endpoint": "https://approved.example.invalid/reference-lookup" },
      "approved-delivery": { "Kind": "httpDelivery", "Endpoint": "https://approved.example.invalid/delivery", "SupportsIdempotency": true }
    }
  }
}
```

An optional `BearerToken` is read only when sending a request. Catalog responses return IDs/kinds only: no root, URL or credential. Configured HTTPS URLs are limited to 2,048 characters, cannot contain userinfo/fragments, and are not taken from request bodies. HTTP is permitted only for loopback test services. Redirects, cookies and proxies are disabled. Restrict configuration access and network egress to approved services. Endpoint/root changes invalidate queued jobs via a non-secret destination binding hash; credential rotation does not change delivery identity.

## Explicit folder intake

`POST /api/connectors/{id}/import` takes `{ workflowId?, workflowVersion?, testRun }`. It scans only the configured root (no request path, no recursion), examines at most 101 directory entries and imports at most 100 files. PDF/PNG/JPEG files are bounded to 20 MiB each and pass the existing file validator. Reparse roots/ancestors and reparse files are rejected. Imports copy accepted original bytes into packet storage and reuse the normal `Store.Accept`/processing pipeline. Content/name/workflow-selection fingerprints provide durable intake deduplication. Inputs are not moved or deleted. Invalid files produce a rejected item; successful items return packet IDs.

## Actual delivery and receipts

`POST /api/connectors/{id}/packets/{packetId}/deliveries` takes `{ packetId, revision }`. Only the exact currently approved revision can be queued; test runs cannot use actual delivery. `requireVerified` additionally requires current reference verification. ZIP bytes, content SHA-256, workflow hash, destination binding hash and a stable idempotency key are committed together to `connector_jobs` in `jobs.sqlite`. A unique connector/packet/revision key prevents double queueing. Payloads are bounded to 50 MiB.

`ConnectorWorker` recovers queued jobs automatically. A SQLite write transaction serializes attempts and prevents approval/revision changes through the bounded external side effect. If the process stops after a receiver accepts but before the ledger commits, the same bytes/key are replayed. HTTP delivery therefore requires an explicitly configured receiver that honors `Idempotency-Key`; exactly-once behavior cannot be promised without that receiver contract. Folder delivery writes a temporary file in the destination, flushes it and atomically renames it. Replayed existing final files must match length/hash. Existing different output is never overwritten.

HTTP delivery POSTs `application/zip` with `Idempotency-Key` and `X-Content-SHA256`. Any 2xx acknowledges **transport only**; bounded response bodies are discarded, never interpreted as business acceptance. Lookup POSTs `{ reference }` and expects an array of `{ reference, recordId, label }`, maximum 100 matches/256 KiB. Delivery response limit is 64 KiB. Both have a 20-second request timeout and no redirects. No real downstream behavior or receipt format is inferred.

When the workflow configures extraction, consume the final approved values from [`fields.json`](approved-fields.md) inside the ZIP. Retries use the original queued ZIP and do not regenerate this projection.

Transient delivery failures retry with bounded backoff, up to five attempts. Approval/configuration/content conflicts become failed jobs. `POST /api/connectors/deliveries/{id}/retry` retries failed jobs using original bytes/key after checking approval again. `GET /api/connectors/deliveries?packetId=...` lists the ledger. These operational/global routes remain administrative by default.

Business acknowledgement is independent: `POST /api/connectors/deliveries/{id}/receipt` takes `{ businessStatus: "accepted" | "rejected", receiptId }`. An operator records an independently obtained business receipt only after transport ACK. The recorded receipt is immutable and attributed to the server-resolved actor. This is not an automatic receiver callback or proof that a downstream business system accepted the ZIP contents.

## Verification

`ReferencePlatformTests` and `ConnectorPlatformTests` exercise immutable versions/hash pins, stale verification, revision carry/invalidation, extracted candidates, synthetic auto-verification exclusion, missing/ambiguous matches, actual configured loopback lookup with redirect/size rejection, folder content deduplication, atomic real ZIP output, separate receipts, stale approval prevention and crash-like ambiguous HTTP cancellation/recovery with stable key/bytes. All files/services used by these tests are disposable synthetic local fixtures; no real customer folder or service is accessed.
