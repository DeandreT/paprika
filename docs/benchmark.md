# Synthetic offline benchmark

Run from the repository root after publishing the application and running `bun install --frozen-lockfile` in `tests/` with Bun 1.4.5:

```powershell
pwsh -File scripts/benchmark.ps1
# Optional: -PackageDirectory <published-folder> -ReportPath <new-json-path> -Python <python-executable>
```

The harness discovers .NET, Bun and Python, starts its own published API on port 5080 in Simulation mode, removes the provider key environment variables and overrides provider keys to empty, waits for readiness, then runs the dedicated Playwright API suite. It stops its process and removes its private temporary application/evaluation data in `finally`. An occupied port is rejected. No browser download is required.

The default output is `artifacts/benchmark-report.json`. Existing reports are preserved; a later default run selects a new unique filename. An explicitly requested existing path is rejected. The retained JSON contains synthetic case names, counts, metrics and review requirements, with no runtime packet/page IDs, timestamps or document text.

## Method

Ten fixed cases in `tests/benchmark-cases.ts` supply real generated PDF files and independent expected document types and ordered source-page memberships. Each initial review-state API response is frozen before any edits. `scripts/evaluate.py` computes page-type accuracy, exact ordered reconstruction, fully correct documents, extra/missed starts, per-type precision/recall and confusion counts. Aggregate ratios use summed counts rather than averaging case percentages. Page-type accuracy follows the evaluator's assigned document type for each page.

Cases cover five clear document types, adjacent distinct documents of the same type, explicit continuation, source-file boundaries, conflicting references, unknown/blank pages, ambiguous type keywords, implicit continuation, interleaved documents and out-of-order pages. Truth is never generated from predictions. Each case describes its expected manual intervention; every case requires human approval in normal use.

After scoring, the test simulates a reviewer applying the independent labels through the review API. It verifies approved exports contain PDF documents and an exact manifest mapping, including noncontiguous and reordered page membership. Original page predictions, reference candidates and the initial document-history snapshot must remain unchanged. Conflicting references must remain candidates rather than being automatically assigned.

The benchmark passes on preservation, approval, provenance and correction invariants. It deliberately has no accuracy pass threshold: imperfect predictions are reported as measured limitations, not relabelled to pass. The dedicated `tests/benchmark.config.ts` selects `*.benchmark.ts`; ordinary `bun --bun run test` / acceptance runs select `*.spec.ts` and exclude this suite.

## Interpretation

The validated packaged run produced 24 pages across 10 cases, with 18 independently labelled documents. Initial proposals correctly typed 23/24 pages (95.83%), reconstructed exact ordered membership for 14/18 documents (77.78%), and got membership, order and type fully correct for 13/18 (72.22%). They proposed 20 documents, with two extra starts and no missed starts. The ambiguous invoice, implicit continuation, interleaving and reversed page order expose the intended limitations. All 24 pages and all 18 corrected document manifests passed the preservation/export checks. The generated report contains the per-case breakdown.

The `publish-platform` release reproduced these results on 2026-11-13. Its report is retained at `artifacts/benchmark-platform-report.json`, with workflow/release snapshot identity per case. Earlier `benchmark-workflows-report.json` and `benchmark-report.json` reports remain preserved. The separate platform acceptance suite contains 30 tests.

Business-reference grouping accuracy is not labelled in this benchmark (`grouping: null`); the conflicting-reference case checks candidate preservation and the absence of automatic assignment. Manual reference assignment is covered separately by the lifecycle acceptance test.

This is a synthetic heuristic benchmark, **not customer accuracy or Azure/Jev provider accuracy**. Text-only generated PDFs do not measure scanned-document OCR, real-world layout/noise, provider calibration or representative document distributions. The default General logistics heuristic cannot reconstruct interleaving or infer implicit continuation from matching references, does not reorder source pages, and treats multiple type keywords as uncertain. Configured workflows can apply explicit identifier and marker policies; those behaviors are verified separately in workflow acceptance tests and are not represented by the default-workflow scores. Live sample evaluation, provider credentials and production-delivery verification remain separate work.

Workflow-aware evaluator results carry the exact workflow/release identity and snapshot hash for each case, plus the test-run marker. Keep reports from different configurations separate when comparing predictions.
