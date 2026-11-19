# Use the packet API

Start the [development API](development.md#run-in-separate-terminals) or the [packaged app](packaging.md). Import **http://127.0.0.1:5080/openapi/v1.json** into an OpenAPI client to see routes, schemas, status codes and binary response types. The server generates the OpenAPI 3.0 document from its routes and public DTOs.

For packet API writes, send `X-paprika: 1`. This is a request marker, not authentication. The legacy `X-PaperworkClassifier: 1` is also accepted. [Shared mode](operations-platform.md) additionally requires an authenticated identity with access to the workflow.

## Upload, review and download

1. `POST /api/packets` as `multipart/form-data` with one `files` part per PDF, PNG or JPEG. Supply an `Idempotency-Key` and reuse it for the same upload if the response is lost. The response contains the queued packet, not the final classification.
2. Poll `GET /api/packets/{id}` until processing completes. Use the returned revision and page IDs for review.
3. `PUT /api/packets/{id}/review` to save or approve. Send the current revision and assign every page to exactly one nonempty document. A `409` means the revision or operation is no longer valid: reload the packet before deciding whether to resubmit. Editing an approved packet clears approval; approve the new revision separately.
4. After approval and any configured release checks, `GET /api/packets/{id}/export` for a ZIP containing document PDFs and the JSON manifest, or `GET /api/packets/{id}/documents/{documentId}/download` for one PDF.

The packet API uses the same operations as [guided classification](classification-guide.md).
