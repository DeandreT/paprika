# Evaluating paprika

The synthetic demo verifies the workflow. Offline Simulation mode can exercise uploaded PDFs/images without provider access; its local text/rule results are not Jev or Azure OCR results. Neither mode measures provider accuracy. Keep simulation results separate from later live-provider evaluations.

## Sample set

Collect representative PDFs/images from approved sources. Confirm the document-type definitions with reviewers, especially signed BOL versus POD. Start with 100–200 packets if available; this is a useful PoC dataset, not statistical proof of production readiness.

Include multipage documents, adjacent documents of the same type, continuation pages, mixed shipments, poor scans, blank pages, unknown types, and interleaved pages. Separate tuning and evaluation by packet and business transaction to avoid putting near-duplicates in both sets.

Keep private paperwork outside the source tree and OneDrive execution directories. Do not commit sample customer documents.

## Ground truth

For each packet, a reviewer labels:

- Every source file and original page number.
- Ordered pages belonging to each document.
- Document type.
- Business reference, if known (automatic business matching is outside this PoC).

An exported manifest from a corrected and approved packet can help prepare the labels, but review it independently; approving a machine prediction is not automatically ground truth.

## Measures

- **Classification:** precision/recall per document type, plus unknown-type handling.
- **Boundaries:** false splits and false merges; explicitly count adjacent same-type documents.
- **Reconstruction:** whole documents whose page membership and ordering are completely correct.
- **Review effort:** packets requiring changes and corrections per packet.
- **Confidence:** compare observed correctness at confidence thresholds on held-out examples. Confidence is not a measured accuracy percentage.
- **Latency:** upload to review-ready, with OCR and classification stages separated when available.
- **Cost:** Azure pages billed plus Jev input/output tokens at the account's current rates. Do not use total packet latency as a billing estimate.

All PoC exports require manual approval. Use a separate offline analysis to estimate a possible future auto-approval policy; do not turn it on based only on synthetic examples or a small favorable sample.

## Provider comparison

Reuse cached OCR for repeat classification experiments. Record source hashes, taxonomy version, prompt version, resolved model name, provider settings, and evaluation-set version. Compare candidates against identical source pages and reviewer labels. Preserve previous predictions when reviewing or comparing results.

## Small offline scoring tool

Before making any reviewer edits, save `GET /api/packets/{id}` as a local `prediction.json`. The tool evaluates the **frozen proposed documents** in that snapshot; a snapshot taken after correction would measure the reviewer instead of the model. Capture it when status is `review`, before the first save. Keep OCR-bearing snapshots in your approved local evidence location, not this repository.

The scorer requires `status: "review"` and integer `revision: 1`; approved, corrected, processing and unverifiable snapshots are rejected. Source page numbers must be positive integers. These checks catch accidental reuse of reviewed output; they are not cryptographic proof of independent labels.

Workflow-aware snapshots retain `{ id, version, releaseId, releaseVersion, hash }` in the report, plus the boolean `testRun` marker. The hash identifies the frozen configuration and must be a 64-character SHA-256 hexadecimal value. Add that same five-field `workflow` object to ground truth to require an exact configuration match. Group results by configuration identity and separate draft tests from published runs; do not pool different workflow versions into an unexplained accuracy result. Older snapshots without this metadata remain supported with `workflow: null`.

Create independent `truth.json` labels using source IDs and original page numbers from the snapshot:

```json
{
  "packetId": "the-packet-id",
  "documents": [
    {
      "type": "bol",
      "pages": [
        { "sourceId": "the-source-id", "pageNumber": 1 },
        { "sourceId": "the-source-id", "pageNumber": 2 }
      ]
    }
  ]
}
```

Include every source page exactly once. Run:

```powershell
python scripts/evaluate.py C:/local-evidence/prediction.json C:/local-evidence/truth.json
```

The aggregate JSON includes page-type accuracy, per-type precision/recall, exact document reconstruction (including order), fully correct documents (including type), and missed/extra document starts. Null precision/recall means there are no applicable examples, not zero accuracy. Start counts are not a substitute for reconstruction metrics on interleaved documents. This script does not measure field accuracy, confidence calibration, service cost, or reviewer effort.

### Optional grouping labels

To measure proposed grouping, add `"businessReference": "SYNTHETIC-LOAD-1"` (or `null` for unassigned) to **every** ground-truth document. Partial grouping labels are rejected; when none are supplied the report's `grouping` is null. The report measures exact reference assignment per page and precision/recall of linked page pairs. Unassigned pages do not form one common group. Pair metrics measure group structure; exact reference accuracy separately detects a structurally correct group assigned to the wrong reference. Neither validates a reference against systems. Current simulation deliberately leaves business references for manual review, so zero proposed grouping is an expected limitation rather than evidence of automatic matching.

### Synthetic regression suite

`scripts/benchmark.ps1` exercises independently labeled synthetic PDFs against the packaged Simulation API, scores initial proposals, and checks that manual correction can reconstruct the approved output. See `docs/benchmark.md` for cases, results and commands. This provides repeatable local regression evidence while sample evaluation remains deferred; its scores must not be reported as customer or live-provider accuracy.

## Graduation criteria

Agree on acceptable error rates, review rate, turnaround, cost, and supported document types with the workflow owner. A production rollout additionally needs shared authentication/authorization, customer access boundaries, retention, monitoring, durable shared storage, operational recovery, and downstream delivery controls.
