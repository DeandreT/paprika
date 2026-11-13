# Quality routing, independent QA and local text training

These additive modules retain manual approval unless an explicitly configured workflow passes every automation gate. Local training is a real multinomial Naive Bayes text classifier. It is not OCR, handwriting recognition, visual recognition, or a claim of production accuracy.

## Integration

- Register `services.AddQualityPlatform()` and map `app.MapQualityPlatform()`. Resolve `QualityStore` once after application build to initialize its tables before catalog mutations.
- Freeze model references inside the existing catalog transaction with `TrainingPlatform.Freeze(connection, transaction, reference)`. Validate shape using `QualityPolicy.Validate(policy)` / `TrainingPlatform.Validate(reference)` and complete automation prerequisites using `QualityValidation.Validate(policy, frozenReference)`. Preserve null capabilities and historical hashes.
- Frozen `TrainingModelRef` includes ID, artifact hash, complete serialized parameters/evaluation and approval state. Catalog freeze ignores caller approval and reads server-retained approval. Runtime resolves the embedded checksum-verified artifact, not the current training catalog. Old snapshots therefore survive model-table changes. Do not permit arbitrary client-supplied frozen artifacts to bypass freeze.
- Call `TrainingStore.Apply(packet)` after text acquisition/classification but before `WorkflowAssembly.Assemble`. This replaces page type/scores with the frozen trained classifier and records per-page model checkpoints. Existing OCR/scanned-text limitations remain. Obsolete heuristic type/boundary notices are removed; unresolved missing-text warnings remain.
- Call `QualityStore.Route(packet, blockers)` after extraction/reference/flow validation. It computes a decision without persisting approval or QA. Main supplies unresolved external validation blockers; missing required field/reference acceptance must not be inferred solely from a classifier score. Use the returned `AutoApprove` to choose approved versus review. Inside the final packet/artifact transaction call `QualityStore.OnProcessed(c,tx,packet,decision)` to save the routing result and create QA only for the finalized approved snapshot. Preserve normal cancellation/concurrency protection. This hook never captures machine predictions as reviewer training labels.
- Inside the review transaction call `QualityStore.OnReview(c,tx,before,after,serverActor)`. It captures approved page labels/text as immutable examples and independently samples the approved revision for QA. Test/demo samples carry synthetic provenance. Simulation mode alone does not imply synthetic source data.
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

## Data, evaluation and approval

`POST /api/training/examples` accepts `{ examples: [{ id, text, label, synthetic, provenance, packetId?, revision? }] }`. Synthetic defaults true. IDs are immutable; importing identical IDs/content is idempotent and changed content conflicts. Only mark examples real when they are authorized independently labeled paperwork. Declared provenance is not independent proof of dataset suitability. Dataset coverage, drift and business acceptance remain responsibilities of the workflow owner.

`POST /api/training/models` accepts `{ name, threshold }`. Each normalized identical-text group occurs on only one side of a deterministic per-label 80/20 split. Conflicting labels for identical text are rejected. Labels with fewer than five distinct texts have no test allocation. Reported accuracy/error bounds cover only held-out predictions at the routing threshold. Small/no qualifying held-out sets explicitly report `not evaluated`. The model is trained on the training split only; it is not silently refit on held-out data after evaluation. A trained model does not update accepted packets.

`POST /api/training/models/{id}/approve` accepts `{ hash }` and records the server-resolved approver. Synthetic or unevaluated artifacts cannot be approved. Approval is separate from meeting a particular quality policy's minimum samples/error bound. `POST /api/training/freeze` embeds the exact retained artifact; `POST /api/quality/validate` validates/freeze-checks a proposed policy/model pair. GET examples/models is administrative because text and token weights can contain sensitive business information.

The panel can import JSON examples, train, inspect evaluation, approve eligible artifacts, configure quality/model settings on a saved workflow draft, inspect frozen QA snapshots, and complete QA. Quality settings use labeled mode, threshold, sampling, evaluation and reviewer controls; JSON is optional advanced editing. Publishing remains in the workflow manager. Unrelated capability settings are preserved.

## Independent QA

Sampling hashes the configured seed, workflow hash and packet ID into a stable uniform fraction. It is independent of classification confidence and review routing. Each sampled approved revision has a separate immutable packet snapshot and revision-checked QA result. Default policy prohibits the primary reviewer from completing their own QA task. Automated primary actor is `automation`.

`GET /api/quality/packets/{packetId}` returns the latest routing result and QA snapshots. `GET /api/quality/qa?packetId=...` lists authorized samples. `POST /api/quality/qa/{id}/complete` accepts `{ revision, verdict: "pass"|"fail", note }`; actor comes only from server identity. A failed sample reopens only the exact currently approved packet revision, saving its old snapshot first. Later packet revisions are not overwritten. QA cannot retract bytes already delivered; existing immutable artifacts remain historical evidence.

Routing results can be recomputed for the same processing revision; QA snapshots and completed results cannot be overwritten. The processing caller must serialize packet finalization with the existing coordinator and ensure no delivery races ahead of a required QA gate. Sampling is monitoring, not mandatory pre-delivery QA for every packet.

## Storage and retention

Owned tables in `jobs.sqlite`: `quality_decisions`, `quality_qa`, `training_examples`, `training_models`, `training_predictions`. `QualityRetentionParticipant` registers all five. Packet-bound routing/QA/checkpoints and untrained examples can be purged transactionally. If retained models coexist with examples linked to a packet, physical purge is conservatively blocked: trained token weights and frozen model snapshots require privacy review, not merely deleting original text. Global models/examples without packet links remain separate explicitly managed records. No background deletion is enabled.

## Validation evidence

The 13 focused backend tests passed using an isolated artifacts directory. Coverage includes uncalibrated defaults, deterministic independent sampling, real-provenance gate execution in an isolated artificial fixture, synthetic/test blocking, boundary/external blockers, duplicate-text leakage prevention, small-data unevaluated state, hash freeze and frozen approval stability, immutable examples, rollback of review capture, independent QA, revision-safe failure handling and retention blockers. Artificial fixtures marked real solely to exercise validation branches are isolated disposable test data and establish no real-world accuracy or delivery authorization.
