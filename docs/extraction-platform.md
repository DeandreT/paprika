# Extraction, visual recognition, preprocessing and enhanced outputs

All operations use local data and `jobs.sqlite`; no external service is invoked by these modules, except AI fields (below), which call OpenAI. Provider OCR geometry is consumed only if already present on a source. The original files are never modified.

## Field and table extraction

The Extraction and images panel supports definition creation without a selected packet, structured field/table editors, packet extraction, evidence inspection, visual-template creation/comparison, image transformation and output generation. It shares the field editor with Guided and Advanced packet review and can also display ad-hoc runs. The workflow capability editor attaches a definition to a workflow.

Definitions have immutable `(id, version)` identities and SHA-256 content hashes. A field selects either a literal label followed by `:`/`=` or a bounded regular expression, with optional named `value` capture. Patterns are limited to 1,000 characters, run with a 100 ms timeout, and pages/candidate counts are bounded. Supported normalization: trimmed text; invariant decimal; `yyyy-MM-dd` or `MM/dd/yyyy` date; yes/no/true/false boolean. Results distinguish `extracted`, `missing`, `ambiguous`, `invalid` and `corrected`. Multiple distinct normalized values remain ambiguous; extraction never silently chooses the first candidate.

AI fields use a `description` (up to 500 characters) instead of a label or pattern, for values without a fixed label such as addresses, carrier, driver or customer names and dates. The description is part of the definition hash; definitions without AI fields keep their earlier hashes. For each matching document, the page text (up to 200,000 characters per document) is sent with the AI field names, kinds and descriptions to `POST {OpenAiBaseUrl}responses` with `model` = `paprika__OpenAiReadModel`, `store: false` and a strict `json_schema` output: one property per AI field, each `{ "value": string|null, "quote": string|null }`. Page text is marked as untrusted data.

Grounding rule: a value is accepted only when `quote` occurs verbatim (ordinal, case-sensitive) in the document's page text and `value` occurs verbatim inside `quote`. Evidence is the value span inside every occurrence of the quote, with page ID, offsets, length, text and page hash taken from the real page text, the same as label matches. A null value is `missing`. A value whose quote is absent, empty or does not contain the value is `invalid` with no value and no evidence. Normalization applies as for other fields; a value that does not normalize is `invalid` with its evidence. Corrections, revision binding and carry-forward are unchanged.

AI fields run only for live packets with OpenAI configured and the exact AI definition enabled by the packet's workflow. The packet-owned cache in `jobs.sqlite` keys answers by definition, document, ordered page content, and extraction contract version. A new answer and its model and usage are recorded together only if the packet is still current and unarchived. Purging the packet also removes its cached answers. Review saves do not send page text: after changing document membership or order, rerun the configured definition from the extraction panel before approval. The rerun fills missing coverage and preserves corrections on other documents.

AI field runs require human approval. A matching quote proves where the value came from, not that the model picked the right value. Before approval, AI predictions do not populate automated flow fields unless a reviewer has corrected them. Put validations and conditions that depend on these values after approval; before approval the field may be absent.

Delimited tables use an exact header line followed by rows with the configured number of columns. Pipe, comma, tab and semicolon delimiters are supported; quoted delimiters and doubled quotes are parsed. Blank/malformed rows terminate a table. This is real delimited-table extraction, not general visual table inference; multi-line quoted cells are not supported. Each field/cell has page ID, original text offsets/length, exact evidence text and full-page text SHA-256. Delimited-cell evidence includes original quote characters while the value is decoded.

Runs bind packet revision and exact workflow hash. Corrections re-read the packet in an immediate SQLite transaction, enforce revision/claim checks, validate evidence hashes/text and document membership, normalize the replacement and append immutable correction/run history. Approval is invalidated and the packet revision advances. Original predictions and run values remain intact. Review carries corrected extraction to a later revision only when the workflow and document ID/type/ordered page IDs are unchanged; changed documents are re-extracted. Re-running configured extraction reuses the current corrected run rather than overwriting human values. `ExtractionPlatform.Current` returns an exact-current run.

### Review a field

Open the packet in Guided review and select a document. For each configured field, compare its delivery value with its source text. A correction needs a free-text note and one choice:

| Choice | What to supply | Result |
| --- | --- | --- |
| **Wrong text selected** (`wrong-selection`) | Select the page and highlight the correct passage in its extracted text. | The chosen span becomes the field's evidence. The definition's field kind determines the normalized value, for example `03/14/2026` becomes `2026-03-14`. |
| **Text read incorrectly** (`transcription`) | Enter the corrected value. | The replacement can differ from the text in the original span. Current evidence is retained, including an empty evidence list when no span was recorded. |
| **Omit from delivery** (`omit`) | Supply a note; there is no replacement value. | The field remains `corrected` with a null value. This decision applies to this document; it does not mark the text absent, redact the PDF, or change the workflow. A required field still needs a usable value before approval. |

**API.** `GET /api/extraction/{packetId}` returns immutable `runs`, `corrections` and `currentConfiguredRunId` (nullable). Use the current packet revision and run ID to `POST /api/extraction/{packetId}/corrections`. For example, if OCR returned `B0L-72`:

```json
{
  "revision": 3,
  "runId": "<current run ID>",
  "documentId": "<document ID>",
  "field": "bol",
  "category": "transcription",
  "value": "BOL-72",
  "reason": "The printed character after B is the letter O.",
  "evidence": []
}
```

`value` is a string interpreted as the configured kind (`text`, `number`, `date` or `boolean`), or JSON `null` for omission. For `wrong-selection`, send exactly one nonempty `evidence` span: `{pageId, offset, length, text, textSha256}`. The offset and length count UTF-16 code units in the original page text; the hash is lowercase SHA-256 of the complete page text encoded as UTF-8. The page must belong to the document. The selection must change the previous evidence, and `value` and `text` must normalize to the same field value. For `transcription` or `omit`, send `[]` to retain the effective evidence; an exact copy of that evidence is also accepted. A `409` requires reloading the current revision and run before submitting another change.

**Reading feedback.** A field correction row links `previousRunId` and `resultId` to immutable runs. New rows retain the raw `correction` (including the note), effective `before` and `after` field snapshots, `fieldKind`, `normalizedValue`, actor and time. For effective evidence, read `after.evidence`, not the possibly empty request array. Older rows can be read through the run links. Requests without a category keep the legacy correction semantics; a missing or null category means *uncategorized*, even if the note or value resembles one of the three choices. Rows recording carry-forward have an `action` instead of a `correction`; new carry rows list the unchanged `documentIds`. Follow those links to distinguish a carried correction from a fresh prediction after regrouping.

These records are review feedback, not approved labels for an extractor. They stay with the packet under its workflow permissions and retention policy. Reprocessing creates a new packet; omission is not a suppression rule for subsequent runs. Consumers should read approved values from [`fields.json`](approved-fields.md), not replay history.

## Visual templates and preprocessing

SkiaSharp 3.119.1 and its Windows native assets are MIT-licensed. Docnet.Core 2.6.0 is MIT-licensed and supplies PDFium native binaries with their accompanying license. Windows x64 native loading and actual PDF rendering are covered by executable tests. Runtime native dependencies must accompany published deployments; a missing native library is a deployment failure, never a silent text-rule fallback.

PDFs are rasterized through PDFium using a fitted 1600×2200 viewport, with a 4-million-pixel page cap, 250-page cap and 64 MiB file cap. PNG/JPEG images decode through SkiaSharp with a 40-megapixel cap. Visual recognition computes 32×32 grayscale-intensity layout features from actual decoded/rendered pixels and compares cosine similarity with labeled immutable templates. It does not use OCR keywords. Features include whitespace/layout and are deliberately simple, not a claim of trained deep visual understanding or measured production accuracy. Blank images return unknown. Scores below a configured threshold, or insufficient margins between competing labels, return `unknown`. Similarities are not calibrated probabilities. All-template diagnostic runs preserve packet predictions; a frozen workflow can explicitly enable prediction override.

Templates bind ID/version, label, source hash, selected source page, feature algorithm, feature values and creation time. A template can be trained from a source page or an existing derivative owned by that same packet/source/page. Template versions are immutable. Snapshots include exact template versions, hashes and feature arrays.

Preprocessing implements actual 0/90/180/270-degree pixel rotation, grayscale conversion and bounded contrast adjustment. Originals and their hashes are preserved; PNG derivatives get independent IDs, dimensions, hashes and transformation metadata under the application-controlled data root. Configured preprocessing runs automatically in the recognition pipeline hook even when recognition itself is disabled; recognition compares those transformed pixels when both are configured. It runs after existing OCR, so it does not retroactively correct OCR text. Automatic orientation detection is not implemented; rotation is explicit. The API supports individual source-page transformations.

## Outputs and approval

Formats:

- `text-json`: reconstructed OCR text with requested literal values removed/replaced.
- `redacted-text-pdf`: a new, searchable standard-font PDF containing only sanitized OCR text; original layout/images/metadata are not imported.
- `searchable-image-pdf`: decoded/rasterized source pixels plus an actual invisible, positioned PDF text layer from provider OCR line polygons. Plain text without geometry is rejected. Latin-1 PDF text is supported; use JSON for Unicode.
- `redacted-image-pdf`: pixel replacement inside explicit page rectangles, followed by re-encoding into a fresh PDF. The entire text layer is omitted. No original PDF objects, embedded files, original image stream, annotations or metadata are copied. Region coordinates are original image pixels or the fitted PDF raster pixels. Inspect derivative dimensions before specifying PDF regions.

Redaction is never a rectangle overlay over retained source content. Pixel-redacted PDF requires at least one explicit region and rejects unknown pages/out-of-bounds regions. Text-only replacement requests are rejected for pixel redaction; searchable image output rejects redaction arguments. Users must identify all sensitive regions; the system does not claim automatic sensitive-data discovery.

Automatic workflow output policies cannot select `redacted-image-pdf`: a global policy has no page-specific reviewed regions. Use the per-packet reviewed output operation with explicit regions after approval instead. Workflow policies also reject `searchable-image-pdf` combined with text redaction values, because retaining source pixels would not remove those values. Choose sanitized text JSON/PDF for automatic text replacement, or per-packet pixel-redacted PDF for reviewed image redaction. Ad-hoc pixel-redacted output remains supported for both PDF and image sources.

Explicit approval of the **current exact revision** is required for API output generation and download, including required reference verification. Old revision files and internal preview artifacts cannot be downloaded through current output routes. Internal flow generation can create `preview` artifacts, but they remain inaccessible; approval advances revision and requires a new exact-revision artifact. An optional `idempotencyKey` on `OutputPlatform.Save` reuses identical packet/revision/request/source/workflow output and rejects conflicting requests under the same key. Files live only beneath controlled `outputs/` and `derivatives/` directories, never caller-supplied paths.

## API and integration hooks

- `GET/POST /api/extraction/definitions`: list/create immutable definitions (administrative).
- `GET /api/extraction/{packetId}`: runs and immutable correction history.
- `POST /api/extraction/{packetId}/run`: `{revision, definitionId, version}`.
- `POST /api/extraction/{packetId}/corrections`: typed field correction with current evidence and reason.
- `GET/POST /api/recognition/templates`: list/train immutable templates (administrative).
- `GET /api/recognition/{packetId}` and `POST .../run`: provenance and diagnostic comparison.
- `GET/POST /api/preprocessing/{packetId}`: list/create derivatives; POST includes `sourceId`, optional `pageNumber` and typed policy.
- `GET /api/preprocessing/{packetId}/derivatives/{id}/image`: ownership-checked PNG download.
- `GET/POST /api/outputs/{packetId}`: list/create approved-revision output artifacts.
- `GET /api/outputs/{packetId}/files/{id}`: current approved output download.

All endpoints are named and have explicit response metadata. Global definitions/templates require administrative access; packet routes carry workflow-scoped permissions. Corrections recheck reviewer claims inside their transaction; ancillary mutations use `ValidateMutation`. Server-resolved identity supplies correction actor history.

Host mapping: `MapExtractionEndpoints`, `MapRecognitionEndpoints`, `MapPreprocessingEndpoints`, `MapOutputEndpoints`. Pipeline: `await RecognitionPlatform.ApplyAsync(store, packet, token)` after OCR/classification and before assembly; `ExtractionPlatform.RunConfigured(store, packet)` after assembly. Review: `ExtractionPlatform.OnReview(connection, transaction, before, after, actor)`. Registry freezer overloads use the supplied SQLite connection/transaction, clone embedded values and require exact hashes. Policies are typed `ExtractionPolicy`, `RecognitionPolicy`, `PreprocessingPolicy`, and `OutputPolicy`; absent capabilities preserve historical behavior/hashes.

`ExtractionRetentionParticipant` declares `extraction_objects`, purges removable packet-bound result/history rows transactionally and blocks physical privacy purge when immutable output/derivative files or source-backed templates still need explicit verified cleanup. Global versioned definitions/templates are retained deliberately. It never calls a logical archive a privacy purge.

## Offline regression coverage

`FieldCorrectionApiTests` exercises packet-scoped history and correction writes, including rejected evidence and stale revisions. Run `bun test tests/field-review.test.ts` from the repository root for source coordinates and typed value handling. With a local simulation server and client running, `bun --bun run test field-correction-review.spec.ts -g "corrects fields"` from `tests/` exercises all three choices through Guided review, reopens the saved packet, approves it and reads the downloaded ZIP.

Other tests cover synthetic labeled forms; normalized/missing/ambiguous fields; genuinely parsed quoted table cells and evidence spans; immutable human corrections and review carry/invalidation; exact definition freeze; visual differentiation/ties/blanks using real synthetic images; real rotation/grayscale/contrast; automatic configured PDF preprocessing without a recognition policy; native PDF rendering; PdfPig-extracted sanitized PDF text; raster-redacted PDF absence of source text and black rendered pixels; positioned invisible OCR text; and output idempotency conflicts. No production calls are used.
