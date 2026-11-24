# Connect your own classification model

Run an adapter in front of your model and configure its full endpoint, model name and bearer key on the paprika server. See [provider setup](providers.md). Paprika does not assume a proprietary inference API or require that your model implement the OpenAI Decisions API.

## Version 1 HTTP contract

Paprika makes one `POST` with `Content-Type: application/json` and `Authorization: Bearer …` per page. Example body:

```json
{
  "schemaVersion": 1,
  "model": "receiving-classifier-v1",
  "instructions": "Classify the current page by visible evidence.",
  "types": [
    { "id": "invoice", "description": "Invoice for goods or services" },
    { "id": "unknown", "description": "Insufficient evidence" }
  ],
  "state": {
    "currentPage": "INVOICE 42\nPage 2 of 2",
    "previousPage": "INVOICE 42\nPage 1 of 2"
  }
}
```

`types` lists the exact labels allowed by that workflow. The state is untrusted source text, not instructions. `previousPage` is empty at the first page of each source file. The request contains text, not file bytes or images. An adapter should treat type selection and document boundaries as independent predictions.

Return a successful JSON response (up to 64 KiB) using the same model name and one allowed type ID:

```json
{
  "schemaVersion": 1,
  "model": "receiving-classifier-v1",
  "documentType": "invoice",
  "confidence": 0.92,
  "startProbability": 0.08
}
```

Both scores must be finite numbers from 0 to 1. Paprika forces a new document at each source boundary, then applies the workflow's split policy. Unexpected model names, invalid scores, and unknown labels fail the run; paprika does not send the page to another provider. Transient HTTP 429 and server errors use the existing durable retries. Valid predictions have the same review and approval requirements as other classifiers.

An administrator sets the endpoint outside the workflow editor. A draft pins the approved endpoint and model when saved. After an endpoint change, create or repair a draft, save and publish it, and select that new version. Older runs do not silently send their text to the new endpoint.
