# Previous workflow release completion record

Verified 2026-11-12. This historical record covers `artifacts/publish-workflows/run-published.ps1`. The subsequent platform expansion and its qualification are recorded in `platform-verification.md`. Earlier releases are preserved.

## Multiple classification workflows delivered

- Reusable versioned document definitions, exact classification releases, and named workflows with draft/edit/publish/archive/new-version/clone/default actions.
- Structured Angular library, release and workflow forms; configured model readiness, ordered stage display, workflow selection/filtering, snapshot-aware review, statistics and saved-draft sample previews.
- Per-type/default classifier, separate, consecutive, manual, fixed-count, first/last-marker and identifier-change splitting; source-local assembly, conservative review for missing/conflicting evidence, and preserved pages.
- Transactionally frozen workflow/release snapshots with SHA-256 identity; stable retries/replay and linked reprocessing; lost-response upload replay survives default changes and archival.
- Simulation preview can freeze draft dependencies, displays test provenance and supports reviewed export. Preview delivery is blocked. Live adapters use the pinned taxonomy and approved model.
- Existing durable Wolverine processing/delivery, approval history, original-source preservation and exports retained. New exports/delivery include workflow provenance. Legacy records/artifacts remain intact.
- Generated OpenAPI **29 operations / 27 paths**, CSharpier, single-origin application package and updated operational documentation.

## Verification

- Isolated backend test run: **121 passed**, zero failures/skips/warnings. CSharpier: **33 files** passed. Regressions cover clone/new-version classification parity and conflicting repeated regex captures found during independent review.
- `python -m unittest discover -s tests -p 'test_*.py'`: **12 evaluator + 2 recovery-evidence tests passed**. Evaluation retains workflow identity/test-run provenance and rejects corrected snapshots; record-evidence checks detect modified/deleted historical rows.
- `scripts/publish.ps1`: Angular production build and .NET Release publish passed. Frontend install audit reported zero vulnerabilities.
- **25 packaged browser/API tests passed across verification runs.** The first full run passed the 12 existing tests and five new API checks. A UI selector and a marker-rule fixture's irrelevant page count were corrected; the targeted run then passed all 13 new workflow tests. Runtime application binaries were unchanged between these runs.
- `scripts/recovery-check.ps1 -PackageDirectory <standalone-copy> -SeedPackageDirectory ./artifacts/publish-durable`: preserved original packet/revision/prediction JSON hashes, source hashes, approved history and immutable delivery artifact hashes after deleting the original data root. Fresh processing/delivery and another startup passed. Public responses retain all prior fields with additive workflow metadata; pre-workflow delivery metadata remains null. Tampering and nine unsafe paths were rejected for the expected reasons.
- `scripts/durability-check.ps1 -PackageDirectory <standalone-copy>`: processed a custom workflow v1, published v2 as default and archived v1, force-killed a real pending delivery retry, verified stopped SQLite state, backed up/restored and observed further attempts/exhaustion. Catalog/default recovered exactly; dead-letter restart/replay and linked reprocessing retained the archived v1 snapshot and approved history.
- `scripts/benchmark.ps1 -ReportPath ./artifacts/benchmark-workflows-report.json`: **10 cases / 24 pages / 18 truth documents**. Initial predictions: **23/24** page types correct, **14/18** exact ordered reconstructions, **13/18** fully correct documents, two extra starts and zero missed starts. Corrected exports matched all 18 document manifests. General logistics baseline behavior is preserved; these are synthetic simulation results.

Verification servers and disposable application/package copies were cleaned up. Intentional transient-failure scenarios emit error-level logs. The final source changes after packaging affect verification scripts/documentation only.

## Scope and tracking

The approved multi-workflow local release is complete. Live OCR/Jev accuracy, configuration parity for a specific deployment, shared deployment and actual downstream integration retain the previously deferred requirements/access dependencies in `docs/requirements.md`. Synthetic results establish reproducible local behavior.

See the [workflow guide](workflows.md) and [workflow API contract](workflows-contract.md).
