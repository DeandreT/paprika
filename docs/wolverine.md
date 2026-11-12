# Wolverine durable messaging

The backend uses **WolverineFx 6.64.0**, **WolverineFx.Sqlite 6.64.0** and **WolverineFx.RuntimeCompilation 6.64.0**. These packages have .NET 10 assets and MIT licenses. RuntimeCompilation bundles Roslyn for handler-adapter compilation; no separate source-generation command or broker is needed for this local release.

## Responsibilities

Wolverine's persisted inbox/outbox, local queues, retries and dead-letter storage handle message orchestration. Packet state, original files, OCR checkpoints, review history, approval snapshots, delivery receipts and artifact hashes remain application records. The user-facing packet inbox is a review screen; it is distinct from Wolverine's message inbox.

Application mutations that create work commit the relevant message in the same SQLite transaction. `DurableQueue` enlists a Wolverine `MessageContext` through `DatabaseEnvelopeTransaction`, sends the command, and commits the business row, generation mapping and envelope together. These local destinations persist into Wolverine's durable inbox through its transactional outbox integration. The post-commit flush is best-effort: failure leaves recoverable work and cannot delete accepted originals. No external outgoing transport is configured. The former recurring business-status polling workers are replaced by durable commands and a startup migration for older data.

Work is linked to an application generation so a delayed message from an earlier retry cannot execute newly queued work. Operator replay atomically claims the old dead letter, resets the appropriate failed business record and persists a new-generation command. The original dead letter is then archived through Wolverine; the claim prevents duplicate replay even if archiving is interrupted.

Processing, simulated delivery and approval continuation have separate queues, each limited to one concurrent handler for this single-process application. Messages carry identifiers rather than files, OCR text or credentials. Handlers reload current business state and retain business-level duplicate/terminal-state protections. Configured folder/HTTP connectors use their separate persistent delivery ledger described in `connectors-platform.md`.

`ContinuePlatformFlow` is committed atomically with approval. Its generation and exact approved revision are checked before execution; completed commands and stale revisions are no-ops. The continuation ledger tracks queued/running/completed/failed work without changing the packet's approved status. Failure replay requires the same currently approved revision, resets failed flow blocks and queues a new generation in one transaction. Deterministic output identity prevents duplicate artifacts after retry. Retention distinguishes permanent terminal-generation records from pending envelopes, active continuation and replayable failures.

## Failures and replay

- Transient HTTP, I/O and timeout failures receive three scheduled retries after 1, 3 and 10 seconds (four business attempts total). Authentication and other permanent failures go directly to fault finalization. Pending retry messages survive restart.
- Exhausted messages remain in Wolverine's dead-letter storage for inspection and operator replay.
- Business rejection is terminal and does not become a transient transport retry.
- Cancellation and prior approvals are not bypassed by delayed or replayed messages.
- Delivery simulation includes `success`, `transient-once`, `transient-always` and `reject`. The repeated-failure scenario will exhaust again after replay; replay does not change its scenario.

Failure finalization is atomic as well: deleting the pending envelope and writing its dead letter share an explicit SQLite transaction. This release uses Wolverine's persistence command builders inside that transaction, rather than relying on separate statements. If dead-letter storage fails, the original envelope remains recoverable; a terminal-failure header is persisted alongside the failed business checkpoint. Fault finalization retries every three seconds until storage recovers, without repeating provider work. This storage-recovery loop is separate from the bounded business retries. A regression test deliberately rejects the dead-letter insert to cover this failure window.

`GET /api/messaging` reports mode `durable-local`, processing/delivery/continuation queue counts and up to 100 eligible current-generation dead letters. Counts include ready, in-flight and scheduled inbox rows, excluding handled deduplication receipts. Pagination skips claimed/superseded letters before applying the result limit, then reconciles their deferred archival through Wolverine; stale entries cannot hide current failures. Each dead letter exposes its message ID/type, related packet/delivery IDs, failure timestamp and a safe reason. Continuation entries additionally identify the approved packet revision. Raw envelopes, document content, provider responses and exception stacks are not exposed.

`POST /api/messaging/deadletters/{id}/replay` requires `X-PaperworkClassifier: 1`. A successful response reports `queued`; missing entries return 404 and stale/in-flight/terminal conflicts return 409. The Angular operations panel provides refresh and replay controls. Its counts are snapshots, not a promise that work cannot change immediately afterward.

## Existing data and recovery

Stop the previous application and take an offline backup before upgrading. Legacy delivery data is imported into the active database non-destructively; original source files, approved histories and delivery ZIP paths are retained. Startup migration must not purge message storage, duplicate old deliveries or re-import stale records on subsequent starts.

Keep earlier release packages, but do not run an older binary against a data directory already changed by a newer version. A rollback uses the earlier release with a separate restored pre-upgrade backup. This local application still supports only one active process per data directory.

Back up the complete stopped data directory, including SQLite WAL files and Wolverine message state. Restoring only packet JSON would discard pending retries and dead letters. See `docs/recovery.md`.

## Repeatable checks

- `scripts/verify.ps1`: formatting, backend durability/transaction/replay tests, evaluator checks and Angular build.
- `scripts/acceptance.ps1 -PackageDirectory <release>`: actual browser/API processing, automatic retry, dead-letter inspection/replay and OpenAPI checks.
- `scripts/recovery-check.ps1 -PackageDirectory <release> -SeedPackageDirectory <previous-release>`: prior data migration, restored originals/history/artifacts, new work and repeated-startup stability.
- `scripts/durability-check.ps1 -PackageDirectory <release>`: force-stop with a real pending retry, use a read-only SQLite probe to prove that generation is still pending with no dead letter, back up/restore into a new directory, verify further attempts and exhaustion, restart with a dead letter, and replay it while preserving approved history. Requires Python 3 for the stopped-state assertion.

No live OCR/Jev accuracy, external downstream acknowledgment, multi-node deployment or exactly-once external delivery is claimed.

## Wolverine license notice

The MIT License (MIT)

Copyright (c) Jeremy D. Miller and Contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
