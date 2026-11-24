# Cross-submission cases and composable flows

## Cases

Enable an embedded `CasePolicy` in a workflow capability with a customer scope, expected document types and optional `autoCollate`. Accepted packet snapshots retain that policy.

Cases are persistent business aggregates, not packet groups inferred from file names. Explicit membership identifies a packet, its reviewed revision and selected document IDs. Each membership retains the document type, workflow hash, source IDs/names/hashes and exact page IDs. Separate submissions can complete one expected-document checklist. A document can belong to only one case; ownership and case/packet revision checks occur in the same SQLite write transaction. Parallel stale updates fail with 409.

The scope is the exact customer ID, workflow identity and test/live-run flag. A manually entered reference remains unverified. The trusted reference handler can call `CaseStore.Collate(packet, CaseVerifiedReference?)`; automatic association requires verified reference evidence, an enabled policy and zero or one compatible verified case. Missing evidence, ambiguous matches and existing unverified cases produce a durable proposal instead. The HTTP API cannot assert verified reference status.

Membership provenance is a snapshot, not an unannounced reinterpretation of later packet edits. `POST /api/cases/{id}/members` with `refresh: true` explicitly refreshes existing members to the checked current packet revision. Trusted verified collation also refreshes stale source revisions; missing/deleted source document identities require explicit review. Every case edit saves its previous revision in `platform_case_history`, including removed or refreshed membership evidence.

Case APIs: authorized list/detail/history, create, checklist edit, add/refresh/remove members and packet-specific collation proposals. Scoped endpoint metadata permits shared reviewers; handlers independently authorize every stored member workflow and hide cases containing missing or archived member packets. History and proposal candidate IDs receive the same filtering. Each mutation uses server-resolved actors and transactionally checks every existing member's workflow/active-claim access plus the incoming source packet. `CasePanelComponent` provides global submission selection or packet-scoped explicit checkboxes, checklist editing, membership removal/refresh and source provenance.

## Typed flow definitions

`FlowPolicy` embeds a complete `FlowDefinition`; a library ID is not a late-bound runtime pointer. The workflow registry validates and hashes the embedded definition before acceptance. Saving a newer reusable library revision cannot rewrite existing accepted packets. `FlowLibrary` edits require the exact expected library revision; immutable accepted snapshots contain their own exact content.

The graph supports 1–64 uniquely identified blocks, bounded dependencies, up to 64 reusable templates and up to 32 safe operations per block. Dependencies must point to earlier blocks in the explicitly ordered definition. Cycles, forward references, unknown enum values, conflicting template overrides and unmapped executable-code properties are rejected. Library definitions are reusable authoring objects; main workflow validation additionally checks required capabilities and taxonomy compatibility before publication/intake.

Supported native blocks:

- `Reference`, `Extraction`, `Quality`, `Case`, `Output`: invoke registered typed application delegates. Main `PlatformPipeline` wires these to the real platform services.
- `Transform`: typed trim, invariant uppercase/lowercase and required-value operations.
- `Validate`: required-value operations only; failure stops downstream execution.

There is no arbitrary code, script, expression evaluator, URL invocation or executable command block.

`FlowCondition` routes on document type, exact field equality or verified reference membership, with optional negation. Fields and verified references come only from completed dependency ancestors, never unrelated sibling blocks. Multiple ancestor field writes resolve in definition order. A false condition records a durable skip. Unconditional descendants of skipped branches remain skipped; a conditional join can explicitly select its own route. All dependencies must have a completed or skipped record before a join runs.

## Runtime and recovery

Call `FlowEngine.ExecuteAsync(packet, IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler>, ct)`. Handlers receive the packet, resolved block, stable idempotency key, ancestor fields and verified references; they return bounded `FlowBlockResult` data. Templates are resolved before delegate invocation. Missing registered handlers fail before creating a run.

`flow_runs` freezes the exact definition and its hash; `flow_blocks` records each block's durable status, attempt, key, timestamp and result. The physical `(packet_id,snapshot_hash)` key uses the canonical `hash:revision:N` composite value. Public contracts expose the original snapshot hash and a separate `PacketRevision`. Old rows are retained. Revision changes create independent graph executions so reviewed field/document changes cannot reuse stale extraction, transformation, validation or output results.

States are pending, running, completed, skipped, failed and waitingApproval. A SQLite transaction claims each block before invocation, preventing two executors from invoking it concurrently. Completed/skipped records are reused on restart. Failed blocks require an explicit revision-checked retry. A crashed running block can be recovered after a 15-minute abandonment window. Retry retains the same packet revision and key, releases the failed block, and atomically queues processing; approved continuations instead queue `ContinuePlatformFlow` while retaining approval.

An `Extraction` block can prepare configured AI fields during asynchronous live packet processing, when its branch is reached. A transient preparation failure returns just that block to pending and preserves the exception for the processing queue's normal retry schedule. Shutdown cancellation leaves the block pending too. Completed ancestors and cached answers for unchanged documents are reused. If the queue exhausts its retries, inspect the packet's dead letter in Messaging and replay it after correcting the cause. Permanent errors in the block still require explicit block recovery.

Review saves, release gates and approved continuations only use existing extraction evidence; they do not request new AI values. If a review edit leaves a document without current AI coverage, save the review without approving, run the configured definition from the extraction panel, check the result, then approve. Uncorrected AI values are not available to flow conditions or operations before approval. Avoid preapproval `RequireValue` operations on these fields; specify required fields in the extraction definition instead. An absent field does not match `FieldEquals`, so a negated equality is not proof of a different reviewed value.

Callbacks that cause side effects **must durably deduplicate their stable idempotency keys**. A process can crash after a side effect but before its completion record commits; no local engine can promise exactly-once remote effects without the receiver's idempotency support. Explicit abandoned-work recovery can overlap a still-running old callback after the abandonment window, so that callback contract is required. Failure records intentionally omit raw exception/provider payloads.

Output blocks suspend before invoking their handler when a packet is unapproved. A `FlowAwaitingApprovalException` from a handler also stores waitingApproval and returns early. Re-entering that unapproved run leaves it unchanged. The approval transaction durably emits `ContinuePlatformFlow(packetId, revision)`; its handler checks that exact approved revision before calling the main approval pipeline. Main output wiring checks the current approval again inside its persistence transaction. New approved revisions recompute the graph and produce exact-revision, idempotent output artifacts.

## Wiring and validation

Register `CaseStore`, `FlowEngine`, `FlowLibrary` and `IOperationsRetentionParticipant -> CaseFlowRetention`; map `MapCaseEndpoints()` and `MapFlowEndpoints()`. The typed API includes reusable library save/list, packet execution detail and explicit retry, with named OpenAPI operations. Flow library authoring requires administrator access; packet routes use workflow access metadata.

`FlowPanelComponent` provides typed block/operation/condition/dependency/template forms and execution recovery. Choose a saved definition in the draft workflow's capabilities editor; that editor owns saving the embedded capability. Saving a library alone does not activate a flow.

Retention declares every owned table. Explicit membership, retained case-history provenance and nonterminal current-revision flow records block packet purge. Removable packet proposals and flow ledgers are deleted in the supplied retention transaction once blockers are clear. Superseded revision graphs cannot resume as the current packet. The reusable library is global configuration, not packet data.

Owned tests cover cross-submission completeness, source evidence, scope/access, stale/concurrent membership, explicit refresh history, verified-only collation, invalid graphs, safe reusable operations, conditional routes, concurrent executors, retry/restart, exact revision isolation, immutable library/accepted definitions and a real `PlatformPipeline` extraction-to-approval/output continuation.
