# Platform release verification

The prioritized local platform expansion is complete and qualified. Verified on 2026-11-13:

- Full backend regression: **226 passed**, zero failed or skipped.
- Evaluator/recovery-evidence suite: **14 passed**.
- CSharpier: **92 files** passed.
- Angular production build passed.
- Frontend dependency audit and NuGet vulnerability check reported no vulnerabilities.
- Fresh framework-dependent .NET Release package and Angular production assets published successfully.
- Full packaged browser/API acceptance: **30 passed in one run**, including generated OpenAPI validation, structured quality/extraction/flow forms, independent QA, references, shared task controls and native PDF imaging/redaction.
- Prior-release migration from `publish-workflows` into a relocated standalone platform package passed. Exact historical packet/revision/prediction records, source bytes, approval history and delivery artifacts survived backup/restore after deleting the original data root. Fresh work and repeated startup passed; tampering and nine unsafe manifest paths were rejected.
- Forced termination with a proven pending delivery retry passed backup/restore, further attempts, failure exhaustion and dead-letter replay. The restored catalog/default and archived workflow snapshot remained intact through linked reprocessing.
- Synthetic benchmark: **10 cases, 24 pages, 18 truth documents**; 23/24 page types, 14/18 exact ordered reconstructions, 13/18 fully correct documents. All 18 corrected manifests matched truth and original predictions remained unchanged. This reproduces the previous baseline.

The first browser run exposed outdated queue-count expectations and exact-label SELECT selectors; corrected tests passed against the final package. An intermittent Windows SQLite fixture cleanup lock was addressed with awaited host disposal, pool-specific cleanup and bounded retry; five repeated focused runs and the full suite passed. Automatic output-policy validation was tightened before the final package was built.

Runnable package: `artifacts/publish-platform`. Prior runnable packages are preserved. Launch `pwsh -NoProfile -File ./artifacts/publish-platform/run-published.ps1` and open `http://127.0.0.1:5080`.

Repeatable checks: `dotnet test server.Tests`, `dotnet csharpier check .`, `python -m unittest discover -s tests -p 'test_*.py'`, and `scripts/publish.ps1`, `scripts/acceptance.ps1`, `scripts/recovery-check.ps1`, `scripts/durability-check.ps1`, `scripts/benchmark.ps1`. Benchmark evidence: `artifacts/benchmark-platform-report.json`.


## Integration regressions covered

- Approval commits a durable continuation; duplicate/stale commands do not duplicate outputs. Terminal continuation failure retains approval and supports exact-revision replay.
- Inferred references and their evidence persist together. Reassembly preserves exact document identity and completed flow effects after restart.
- Changed/split documents receive complete configured extraction. Carried human corrections remain intact; missing required values block approval and export.
- Deterministic output files recover after a rolled-back database write. Divergent bytes or request identity are rejected.
- Output bytes are generated/read while the current approved revision is protected from concurrent edits.
- Shared case and QA access is workflow-scoped and excludes archived or missing source dependencies. Public QA snapshots omit provider internals.
- Learned-model splitting deduplicates corrected/source-page examples before held-out evaluation.
- Reference lookup and real delivery reject changed connection bindings; local synthetic reference evidence remains distinct from real delivery evidence.
- Automatic output policies reject pixel redaction without per-packet reviewed regions and incompatible searchable/redaction settings. Packaged native-image acceptance exercises the supported reviewed-region path.

An independent source recheck confirmed the original continuation, reference-recovery and extraction-coverage findings are resolved.

Live OCR/Jev accuracy, representative real paperwork, actual downstream contracts and production hosting acceptance remain separate validation work. Synthetic and isolated local connector tests establish software behavior only.
