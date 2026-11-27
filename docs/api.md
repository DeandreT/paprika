# Use the packet API

Start the [development API](development.md#run-in-separate-terminals) or the [packaged app](packaging.md). Import **http://127.0.0.1:5080/openapi/v1.json** into an OpenAPI client to see routes, schemas, status codes and binary response types. The server generates the OpenAPI 3.0 document from its routes and public DTOs.

For packet API writes, send `X-paprika: 1`. This is a request marker, not authentication. The legacy `X-PaperworkClassifier: 1` is also accepted. [Shared mode](operations-platform.md) additionally requires an authenticated identity with access to the workflow.

## Upload, review and download

1. `POST /api/packets` as `multipart/form-data` with one `files` part per PDF, PNG or JPEG. Supply an `Idempotency-Key` and reuse it for the same upload if the response is lost. The response contains the queued packet, not the final classification.
2. Poll `GET /api/packets/{id}` until processing completes. Use the returned revision and page IDs for review.
3. `PUT /api/packets/{id}/review` to save or approve. Send the current revision and assign every page to exactly one nonempty document. A `409` means the revision or operation is no longer valid: reload the packet before deciding whether to resubmit. Editing an approved packet clears approval; approve the new revision separately.
4. After approval and any configured release checks, `GET /api/packets/{id}/export` for a ZIP containing document PDFs and the JSON manifest, or `GET /api/packets/{id}/documents/{documentId}/download` for one PDF. When the workflow configures extraction, read [`fields.json`](approved-fields.md) for the final approved field values.

The packet API uses the same operations as [guided classification](classification-guide.md).

## Local page-type classifier datasets

Training administration requires administrator access in shared mode. Use `GET /api/training/datasets` to list `{id, name}` pairs. Create a collection with `POST /api/training/datasets` and `{"name":"Synthetic invoices"}`. Use its returned **ID**, not the display name, in subsequent URLs.

| Operation | Route | Request |
| --- | --- | --- |
| List member IDs, labels and synthetic flags only | `GET /api/training/datasets/{datasetId}/examples/summary` | |
| List members, including labeled text | `GET /api/training/datasets/{datasetId}/examples` | |
| Import immutable members | `POST /api/training/datasets/{datasetId}/examples` | `{"examples":[{"id":"sample-1","text":"Synthetic invoice payment due","label":"invoice","synthetic":true,"provenance":"Handwritten test fixture"}]}` |
| Train and evaluate on these members | `POST /api/training/datasets/{datasetId}/models` | `{"name":"Invoice desk model","threshold":0.95}` |
| Rename a named dataset | `PUT /api/training/datasets/{datasetId}` | `{"expectedName":"Synthetic invoices","name":"Invoice desk examples"}` |
| Import original saved provider decisions as candidates | `POST /api/training/datasets/{datasetId}/candidates/import` | `{"packetIds":["<packet-id>"]}` |
| Inspect candidate groups, text, teacher evidence and latest review | `GET /api/training/datasets/{datasetId}/candidates` | |
| Save a batch of candidate decisions | `POST /api/training/datasets/{datasetId}/candidates/review` | `{"decisions":[{"id":"<candidate-id>","version":"<from GET>","decision":"accepted","label":"invoice"}]}` |
| Download reviewed examples, including training text | `GET /api/training/datasets/{datasetId}/export` | JSON attachment, [format and split rules](quality-platform.md#portable-reviewed-label-export) |
| List retained models across datasets | `GET /api/training/models` | |

The dataset ID belongs in the URL. A `datasetId` query parameter, a conflicting ID in an import/train request, or an unknown target fails the request. Omit the dataset path **only** for the original `GET/POST /api/training/examples` and `POST /api/training/models` compatibility operations: these always target the collection with ID `default` (display name **Legacy**). An import into another dataset never becomes a legacy example.

Model responses include the dataset at training time in `artifact.dataset`; a pre-dataset artifact lacks that property and belongs to Legacy. For workflow review collection, set `workflow.capabilities.reviewCollection` on a draft to `{"datasetId":"<target ID>"}`. See [Quality and local training](quality-platform.md#choose-a-training-dataset) for review defaults, provenance and publication.
