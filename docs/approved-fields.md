# Consume approved fields

When a workflow has an extraction definition, its approved packet ZIP includes `fields.json` alongside the PDFs and `manifest.json`. HTTP and folder delivery carry the same `fields.json` as the download. Read it for the final values; `platform.json` is evidence, not the receiving contract.

## Version 1

| Property | Meaning |
| --- | --- |
| `schemaVersion` | `1`. A consumer should reject versions it doesn't support. |
| `packetId`, `revision`, `workflowHash` | Identify the approved packet snapshot. |
| `definition` | Exact extraction definition: `id`, `version`, `sha256`. |
| `documents` | One entry per approved document, in packet order. Join `documentId` to `documents[].id` in `manifest.json` for the PDF filename and page order. `type` is the workflow's document type ID. |
| `documents[].fields` | Configured fields in definition order; empty for a document outside the definition's document-type scope. |
| `fields[].name`, `kind`, `value`, `status` | `kind` is `text`, `number`, `date`, or `boolean`. Non-null values are normalized strings. `corrected` identifies a reviewer correction and has `value: null` if the reviewer cleared an optional field. `extracted` identifies an extracted value; `missing` has `value: null` for an optional field that wasn't found. |
| `fields[].evidence` | Source references (`pageId`, `sourceId`, `pageNumber`, `offset`, `length`). Page numbers are one-based in the original source. Offsets and lengths are UTF-16 code units in paprika's extracted page text, not image coordinates. A correction can retain the original printed span or have no span. |

There are no extraction-history rows, table cells, or raw evidence excerpts in this projection. A packet without configured extraction has no `fields.json`. An unapproved revision or a revision that fails required field coverage cannot be exported as approved.

A reviewer choosing **Omit from delivery** on an optional field produces `"status": "corrected", "value": null`. The field stays in the array and may retain evidence pointing to text that was present. **Wrong text selected** moves the evidence reference to the reviewer's selected span. **Text read incorrectly** keeps the reference even when the approved value differs from that text. Categories and notes are available in [packet correction history](extraction-platform.md#review-a-field), not in the v1 projection. A legacy cleared value has the same downstream representation as an omission; do not infer a review category from `fields.json`.

Previously queued connector jobs keep their original bytes and idempotency key. A job created by an older version of paprika may therefore still deliver a ZIP without `fields.json` when retried.

## Receiving workflow

Start with the [receiving field definition](examples/receiving-fields.json). An administrator can post it to `/api/extraction/definitions` with `X-paprika: 1`, then attach that version to a workflow's extraction settings and publish the workflow. The example targets `bol`; adjust `documentType` to match your workflow before creating the definition. Use text kind for identifiers so leading zeros are preserved.

| Field name | Source label |
| --- | --- |
| `shipment_id` | `Shipment ID` |
| `bol` | `BOL` |
| `po` | `PO` |
| `carrier` | `Carrier` |
| `pickup_address` | `Pickup address` |
| `delivery_address` | `Delivery address` |

Set required fields based on the receiving contract. If names or addresses aren't labeled, consider [AI fields](extraction-platform.md#field-and-table-extraction) and verify them during review. Approve the corrected packet before delivery.

An HTTP receiver should compute the content SHA-256, verify it against `X-Content-SHA256`, and store it with the `Idempotency-Key` and accepted record. A retry with the same key and bytes must return the same acknowledgement without creating another record. Reject a reused key with different bytes. The sender's `2xx` only confirms transport; record a [business receipt](connectors-platform.md#actual-delivery-and-receipts) after confirming the receiving system accepted the fields.
