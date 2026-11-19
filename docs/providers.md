# Configure live document processing

**Simulation** reads embedded PDF text locally and proposes types and page boundaries using deterministic rules. Pages with no text remain unknown for review. **Try demo packet** creates a synthetic packet with fixed predictions. Neither requires a key, and neither measures live accuracy.

**Live** needs both a page reader and a classifier. Choose providers approved to receive the source documents or their contents. A Live run that can't use its providers reports a failure; it doesn't substitute simulation results.

## Choose providers

| Step | First choice when configured | OpenAI alternative |
| --- | --- | --- |
| Read pages | Azure Document Intelligence receives source files. Set `paprika__AzureEndpoint` (HTTPS) and `paprika__AzureKey`. | OpenAI uses embedded PDF text when a page has at least 40 letters or digits. Otherwise paprika sends a rendered page to the Responses API for transcription with `store: false`. |
| Classify and split | TypeSafe Jev receives page text and context. Set `paprika__JevApiKey`; `paprika__JevModel` defaults to `jev-latest`. | OpenAI receives page text and context. Its Decisions API returns a document-type choice and a separate decision about whether a new document starts here. |

Provider selection is independent for each step. With only `OPENAI_API_KEY` (or `paprika__OpenAiApiKey`) set, OpenAI handles both. For OpenAI-only setups, leave the Azure and Jev settings unset, including `paprika__JevModel`.

`paprika__OpenAiModel` selects the Decisions classifier; `paprika__OpenAiReadModel` selects the vision reader. Both default to `gpt-6-luna`. `paprika__OpenAiBaseUrl` overrides the API base URL (`https://api.openai.com/v1`); change it only for a trusted endpoint that supports the required APIs.

Azure uses Document Intelligence API version `2024-11-30`. Its free tier reads only the first two pages; use a tier that covers the entire file. A provider score is evidence for review, not a measured accuracy rate.

## Start the backend and select a workflow

1. Map the chosen key variables from a secret store into **only the running API process**. Keep them out of source, command arguments, the browser, build jobs and frontend terminals. For Azure also set the non-secret HTTPS resource endpoint.
2. To start a **new** catalog in Live mode, set `paprika__Mode=Live` before launch. The default is `Simulation`; legacy `Demo` also seeds Simulation. For an existing catalog, the variable won't change a published workflow.
3. Start the [packaged launcher](packaging.md) with this environment, or start the [API separately](development.md#run-in-separate-terminals). Restart the API after changing provider settings. Readiness in the UI means configuration is present; it doesn't verify access or quota.
4. In [workflow management](workflows.md), publish and select a **Live** workflow with the server-reported `liveModel`. Draft previews still run in Simulation. Upload an approved sample and check the returned page count, text, types and boundaries.

An accepted packet keeps its workflow snapshot on retry and ordinary reprocessing. Explicitly selecting a different workflow creates a linked run. Use [held-out paperwork](evaluation.md) to measure performance before relying on the results.

Provider references: [Azure Document Intelligence](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/), [Jev API](https://docs.typesafe.ai/api/system-one.md), [OpenAI Decisions API](https://developers.openai.com/api/docs/guides/decisions).
