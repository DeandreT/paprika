# Quality routing, independent QA and local text training

These additive modules retain manual approval unless an explicitly configured workflow passes every automation gate. Local training is a real multinomial Naive Bayes text classifier. It is not OCR, handwriting recognition, visual recognition, or a claim of production accuracy.

## Integration

- Register `services.AddQualityPlatform()` and map `app.MapQualityPlatform()`. Resolve `QualityStore` once after application build to initialize its tables before catalog mutations.
- Freeze model references inside the existing catalog transaction with `TrainingPlatform.Freeze(connection, transaction, reference)`. Validate shape using `QualityPolicy.Validate(policy)` / `TrainingPlatform.Validate(reference)` and complete automation prerequisites using `QualityValidation.Validate(policy, frozenReference)`. Preserve null capabilities and historical hashes.
- Frozen `TrainingModelRef` includes ID, artifact hash, complete serialized parameters/evaluation and approval state. Catalog freeze ignores caller approval and reads server-retained approval. Runtime resolves the embedded checksum-verified artifact, not the current training catalog. Old snapshots therefore survive model-table changes. Do not permit arbitrary client-supplied frozen artifacts to bypass freeze.
- Call `TrainingStore.Apply(packet)` after text acquisition/classification but before `WorkflowAssembly.Assemble`. This replaces page type/scores with the frozen trained classifier and records per-page model checkpoints. Existing OCR/scanned-text limitations remain. Obsolete heuristic type/boundary notices are removed; unresolved missing-text warnings remain.
- Call `QualityStore.Route(packet, blockers)` after extraction/reference/flow validation. It computes a decision without persisting approval or QA. Main supplies unresolved external validation blockers; missing required field/reference acceptance must not be inferred solely from a classifier score. Use the returned `AutoApprove` to choose approved versus review. Inside the final packet/artifact transaction call `QualityStore.OnProcessed(c,tx,packet,decision)` to save the routing result and create QA only for the finalized approved snapshot. Preserve normal cancellation/concurrency protection. This hook never captures machine predictions as reviewer training labels.
- Inside the review transaction call `QualityStore.OnReview(c,tx,before,after,serverActor)`. It captures approved page labels/text into the workflow's review collection when applicable and independently samples the approved revision for QA. Test/demo samples carry synthetic provenance. Simulation mode alone does not imply synthetic source data.
- `QualityPanelComponent` (`app-quality-panel`) accepts `packetId`, `revision`; emits `refreshed`. With empty packet ID it exposes global dataset/model/draft-policy actions.

## Policy

Example conservative draft settings:

```json
{"mode":"manual","autoApprove":false,"threshold":0.95,"sampleRate":0.1,"sampleSeed":"quality-v1","minimumHeldOut":50,"maximumErrorUpperBound":0.1,"syntheticEvaluation":false,"requireIndependentReviewer":true}
```

`selective` mode lists page/document issues rather than making every raw score authoritative. Enabling `autoApprove` additionally requires:

1. A server-approved embedded model trained exclusively on declared real examples.
2. Held-out evaluation with at least `minimumHeldOut` threshold-qualified examples, a Wilson 95% error upper bound at or below `maximumErrorUpperBound`, and exactly the evaluated routing threshold. This is an aggregate validation gate, not an assertion that individual scores are calibrated probabilities.
3. Independent QA with nonzero sampling and a separate reviewer.
4. Actual matching model checkpoints for every current page, adequate text, known types, complete page membership and no unresolved validation issues.
5. Deterministic `separate` or complete `fixed` splitting. Text classification evaluation does not establish probabilistic document-boundary accuracy.
6. An operational packet, not a test/demo run or deliberate synthetic-evaluation policy. Synthetic artifacts cannot enable automated approval.

Real authorized paperwork may be processed offline in simulation mode and then classified by the trained model. This can pass the gates; raw simulation rules cannot. Synthetic evaluation policies remain manual and never authorize operational delivery. Any downstream delivery path must also enforce the shared synthetic/test provenance restrictions.

## Choose a training dataset

In **Platform tools → Quality and training**, open **Quality, independent QA and trained text models**. Choose an existing classifier dataset or create one. Import a JSON array of labeled examples into that selection, inspect its members, then choose **Train and evaluate**. Use at least two labels with distinct text. Workflow attachment and publication are separate actions.

Each dataset has a permanent ID. Example IDs are immutable **within** a dataset. The same ID or text can occur in another dataset with a different label; only members of the selected dataset participate in deduplication, evaluation and training. Re-importing identical rows within a dataset is safe; conflicting content fails the entire batch. The Legacy dataset (ID `default`) contains pre-upgrade examples. Its name is reserved. The original import, list and train routes continue to target only Legacy.

New model artifacts record the dataset ID and name as they were at training time. `datasetExampleCount` and `datasetSnapshotHash` describe **all** members before deduplication, including their IDs and provenance. The existing `datasetHash` still describes the unique labeled texts used for splitting. Renaming the dataset or importing more examples cannot change the model's parameters, recorded provenance or hash. Older artifacts remain byte-compatible, with no new provenance fields inserted. See [training API routes and request bodies](api.md#local-page-type-classifier-datasets).

### Collect human-approved page labels

On a saved workflow draft, choose the **review collection** separately from the trained model. Set it to a dataset to send future approved examples there, even if the classifier was trained elsewhere. The workflow freezes the target ID at publication; renaming the target does not redirect any packets. An explicit empty or unknown target is rejected. Simulated test runs and demo packets keep their synthetic flag. Automatic approvals never provide training labels.

Without an explicit review collection, existing workflows (no model, a historical model or a Legacy-trained model) continue capturing human approvals into Legacy. A workflow using a model from a **named** dataset collects **nothing** until a target is explicitly selected. Importing examples, choosing a dataset in the panel, training a model or collecting reviews does not retrain a model, publish a draft or change a packet's frozen classifier.

## Data, evaluation and approval

### Review predictions as teacher labels

Select a dataset and open **Create candidate labels from predictions**. Choose a source packet in the inbox, then import its saved predictions. Repeat for other runs you want to compare. This reads existing evidence only; opening the queue, importing, reviewing, training and downloading never dispatch another provider request. Local mode uses the local administrator; in shared mode these operations require administrator access.

The review queue shows the original proposed type, source file and page, packet/revision, text, provider, requested model and reported model when recorded. New captures bind the provider response to the page **before** a trained classifier or recognition override can change the displayed type. An older completed live snapshot can supply an unattributed proposal when no overlay obscured it. If an overlay was used and its original provider prediction cannot be recovered, that page is skipped. Simulation rules, demo labels and later packet-review edits do not supply teacher predictions.

Disagreements come first. Independently recorded teachers are compared only across different packet runs with matching source bytes, page number, current and previous page text, released taxonomy, instructions and test/source status. A second run of the same teacher, a matching file name or similar text is insufficient. Two different providers, or distinct **reported** models from the same provider, can support a comparison. Legacy evidence lacking the comparison key stays unpaired. Filter by comparison or review status, sample an agreement, or inspect any row's text and all proposals.

Stage **Accept** with an allowed proposed type, **Correct** with another type from the original taxonomy, or **Reject** without a label. `unknown` is reviewable but cannot become the training target. Save up to 100 decisions together. The server checks the evidence membership and review version of every row in one transaction; on conflict refresh before submitting again. Adding new evidence to a reviewed group makes it pending until reviewed again. Edits replace that group's current decision; repeated import does not add votes or examples.

Only accepted or corrected decisions enter the selected dataset's next fit or export. They stay separate from immutable labeled imports, including when the reviewer corrects the teacher. Saving a review does not train, approve or publish a model. Model source dependencies remain attached to all contributing packets even if the label is subsequently changed or rejected. An archived packet is excluded from future candidate reads, fits and downloads; a physical purge removes packet-bound observations and any dependent review, subject to the existing retention checks.

### Portable reviewed-label export

**Download JSON with training text** saves a UTF-8 `application/json` attachment locally. It contains source text and reviewer provenance. There is no external trainer URL, upload, or automatic model import. Example reader for a local trainer:

```python
import json
from pathlib import Path

data = json.loads(Path("paprika-default-reviewed.json").read_text(encoding="utf-8"))
assert data["format"] == "paprika.reviewed-page-labels.v1"
train = [(r["example"]["text"], r["example"]["label"])
         for r in data["examples"] if r["split"] == "train"]
held_out = [r for r in data["examples"] if r["split"] == "held-out"]
```

- `dataset` contains the selected ID and current name. `examples[]` contains manual labeled members and reviewed teachers. `example` has `id`, `text`, `label`, `synthetic`, `provenance` and optional source/approval fields. `origin` is `labeled-example` or `reviewed-teacher`. For a teacher, `teacherReview` preserves the original proposals (`predictions[]`), source hashes, identifiers, attribution, reviewer, reviewed label, decision, version and time. `example.label` is the reviewer's target. A missing reported `model` stays null even when `requestedModel` is known. A teacher-derived example cannot be re-imported under its `teacher:` ID as independent ground truth.
- `groupId` and `split` are the partition the local fit uses. With teacher inputs, whole related source groups (including all contributing packets, file hashes and detected near-copies) stay together. Teacher-linked groups are never independent evaluation data, even if an independently supplied label is preferred for an overlapping page. Only `train` is fit data; only `held-out` is an independent evaluation representative. `held-out-related` is withheld, without another vote. `duplicate`, `superseded` and `conflict` rows are available for provenance, never for fitting; resolve conflicts before training. Do not pool splits across separate exports or assume differently worded sources are independent without checking their origin.
- `fieldFeedback[]` is **separate evidence**, never a page-type target. It includes explicit field corrections tied to grounded suggestions on reviewed candidate page versions. Each record identifies the packet, document, synthetic status, extraction definition/version/hash, field and kind. `suggestion` retains provider/model attribution, answer key, original value and quote, and verbatim source evidence (page ID, UTF-16 offset and length, text and full-page text hash). `review` retains the actor, timestamp, revision, prior/result run IDs, category, submitted value and effective `before`/`after` values and spans. Only the latest explicit wrong-selection or transcription can carry a normalized `extractionTarget`. A business omission, an uncategorized edit or a superseded edit has **no target property**. Ungrounded, unreviewed or unverifiable suggestions are not extraction targets. Paprika does not fit a field-value model from this file.

The format can represent an empty dataset or a dataset too small to evaluate. Exporting is not an accuracy claim.

Example imports accept `{ examples: [{ id, text, label, synthetic, provenance, packetId?, revision? }] }`. Synthetic defaults true. Only mark examples real when they are authorized independently labeled paperwork. Declared provenance is not independent proof of dataset suitability. Dataset coverage, drift and business acceptance remain responsibilities of the workflow owner.

Training accepts `{ name, threshold }`. Each normalized identical-text group occurs on only one side of a deterministic per-label 80/20 split. Conflicting labels for identical text in this dataset are rejected. Labels with fewer than five distinct texts have no test allocation. Reported accuracy/error bounds cover only held-out predictions at the routing threshold. Small/no qualifying held-out sets explicitly report `not evaluated`. The model is trained on the training split only; it is not silently refit on held-out data after evaluation. A trained model does not update accepted packets.

`POST /api/training/models/{id}/approve` accepts `{ hash }` and records the server-resolved approver. Synthetic or unevaluated artifacts cannot be approved. Approval is separate from meeting a particular quality policy's minimum samples/error bound. `POST /api/training/freeze` embeds the exact retained artifact; `POST /api/quality/validate` validates/freeze-checks a proposed policy/model pair. GET examples/models is administrative because text and token weights can contain sensitive business information.

The panel can import JSON examples, train, inspect evaluation, approve eligible artifacts, configure quality/model settings on a saved workflow draft, inspect frozen QA snapshots, and complete QA. Quality settings use labeled mode, threshold, sampling, evaluation and reviewer controls; JSON is optional advanced editing. Publishing remains in the workflow manager. Unrelated capability settings are preserved.

## Independent QA

Sampling hashes the configured seed, workflow hash and packet ID into a stable uniform fraction. It is independent of classification confidence and review routing. Each sampled approved revision has a separate immutable packet snapshot and revision-checked QA result. Default policy prohibits the primary reviewer from completing their own QA task. Automated primary actor is `automation`.

`GET /api/quality/packets/{packetId}` returns the latest routing result and QA snapshots. `GET /api/quality/qa?packetId=...` lists authorized samples. `POST /api/quality/qa/{id}/complete` accepts `{ revision, verdict: "pass"|"fail", note }`; actor comes only from server identity. A failed sample reopens only the exact currently approved packet revision, saving its old snapshot first. Later packet revisions are not overwritten. QA cannot retract bytes already delivered; existing immutable artifacts remain historical evidence.

Routing results can be recomputed for the same processing revision; QA snapshots and completed results cannot be overwritten. The processing caller must serialize packet finalization with the existing coordinator and ensure no delivery races ahead of a required QA gate. Sampling is monitoring, not mandatory pre-delivery QA for every packet.

## Storage and retention

Owned tables in `jobs.sqlite`: `quality_decisions`, `quality_qa`, `training_datasets`, `training_examples`, `training_models`, `training_predictions`. `QualityRetentionParticipant` registers them for retention. Packet-bound routing/QA/checkpoints and untrained examples can be purged transactionally. If retained models coexist with examples linked to a packet, physical purge is conservatively blocked: trained token weights and frozen model snapshots require privacy review, not merely deleting original text. Global models/examples without packet links remain separate explicitly managed records. No background deletion is enabled.

## Validation evidence

The 13 focused backend tests passed using an isolated artifacts directory. Coverage includes uncalibrated defaults, deterministic independent sampling, real-provenance gate execution in an isolated artificial fixture, synthetic/test blocking, boundary/external blockers, duplicate-text leakage prevention, small-data unevaluated state, hash freeze and frozen approval stability, immutable examples, rollback of review capture, independent QA, revision-safe failure handling and retention blockers. Artificial fixtures marked real solely to exercise validation branches are isolated disposable test data and establish no real-world accuracy or delivery authorization.
