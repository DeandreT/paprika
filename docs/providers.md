# Configure live document processing

**Simulation** reads embedded PDF text locally and proposes types and page boundaries using deterministic rules. Pages with no text remain unknown for review. **Try demo packet** creates a synthetic packet with fixed predictions. Neither requires a key, and neither measures live accuracy.

**Live** needs both a page reader and a classifier. Choose providers approved to receive the source documents or their contents. A Live run that can't use its providers reports a failure; it doesn't substitute simulation results.

## Choose providers

In a live workflow, select **Jev**, **OpenAI Decisions**, or a configured **self-hosted classifier**. Each takes page text and context and returns the document type and whether a new document starts. Publishing a workflow pins its classifier and model; adding or removing a key does not switch it to another service.

| Classifier | Server key | Approved model (default) |
| --- | --- | --- |
| Jev | `paprika__JevApiKey` | `paprika__JevModel` (`jev-latest`) |
| OpenAI Decisions | `paprika__OpenAiApiKey` or `OPENAI_API_KEY` | `paprika__OpenAiModel` (`gpt-6-luna`) |
| Self-hosted | `paprika__CustomClassifier__ApiKey` | `paprika__CustomClassifier__Model` (required) |

Page reading is independent of that choice. When `paprika__AzureEndpoint` (HTTPS) and `paprika__AzureKey` are configured, Azure Document Intelligence reads the source. Otherwise the OpenAI reader uses embedded PDF text on pages with at least 40 letters or digits and sends other pages to the Responses API for transcription with `store: false`. `paprika__OpenAiReadModel` selects the vision reader (default `gpt-6-luna`); `paprika__OpenAiBaseUrl` overrides the OpenAI API base (`https://api.openai.com/v1`) and must point to a trusted endpoint. Extraction definitions with AI fields also send a document's page text and the field descriptions to the Responses API with the same model, base URL and `store: false`; see [extraction](extraction-platform.md).

Azure uses Document Intelligence API version `2024-11-30`. Its free tier reads only the first two pages; use a tier that covers the entire file. A provider score is evidence for review, not a measured accuracy rate.

## Start the backend and select a workflow

1. Map the chosen key variables from a secret store into **only the running API process**. Keep them out of source, command arguments, the browser, build jobs and frontend terminals. For Azure also set the non-secret HTTPS resource endpoint.
2. To start a **new** catalog in Live mode, set `paprika__Mode=Live` before launch. The default is `Simulation`; legacy `Demo` also seeds Simulation. For an existing catalog, the variable won't change a published workflow.
3. Start the [packaged launcher](packaging.md) with this environment, or start the [API separately](development.md#run-in-separate-terminals). Restart the API after changing provider settings. Readiness in the UI means configuration is present; it doesn't verify access or quota.
4. In [workflow management](workflows.md), select **Live**, choose the classifier, check its model, then publish and select that workflow. Intake requires a configured page reader and the selected classifier's key. To change providers for a published workflow, create a new version. Draft previews are simulation-only.

An accepted packet keeps its workflow snapshot on retry and ordinary reprocessing. Explicitly selecting a different workflow creates a linked run. Use [held-out paperwork](evaluation.md) to measure performance before relying on the results.

For an older live workflow with no explicit classifier, paprika checks its saved model against both configured model names (exact match) and the `jev-` / `gpt-` prefixes (case-insensitive). Exactly one provider must match. A missing model, an unknown alias, or conflicting matches require a new workflow version with an explicit provider and approved model. Changing keys is not a way to resolve an ambiguous workflow. Records that predate workflow snapshots entirely retain the legacy server-default behavior.

## Connect a self-hosted classifier

Set `paprika__CustomClassifier__Endpoint` to the full URL of an endpoint implementing the [classifier contract](custom-classifier.md), and set the model and key above. Use HTTPS, or HTTP only with a loopback endpoint for local development. Keep the key in the running server's secret store. Paprika pins a fingerprint of the endpoint and model; changing either requires publishing a new workflow version. Key rotation alone does not require a new version.

Only select this provider for workflows authorized to send extracted text to that endpoint. It replaces the classification step, not OCR or OpenAI-backed AI fields.

Provider references: [Azure Document Intelligence](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/), [Jev API](https://docs.typesafe.ai/api/system-one.md), [OpenAI Decisions API](https://developers.openai.com/api/docs/guides/decisions).
