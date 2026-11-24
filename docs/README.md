# paprika documentation

## Get running

- [First run on Windows](../README.md#first-run-on-windows) — start from source and try a packet locally.
- [Run in separate terminals](development.md#run-in-separate-terminals) or [build a Windows package](packaging.md) that serves the API and UI together.
- [Run the Linux container](container.md) — build and smoke-test the image, configure access and persist data.
- [Choose where documents are stored](storage.md) — use the local filesystem or a private Azure Blob container.
- [Configure live providers](providers.md) — choose Azure OCR, Jev or OpenAI and publish a Live workflow.

## Process paperwork

- [Review your first packet](classification-guide.md) — inspect source pages, correct groups, approve and export.
- [Configure a workflow](workflows.md) — define document types, choose splitting rules and preview a draft before publishing it.
- [Verify business references and deliver output](connectors-platform.md) using a dataset, folder or HTTP service.
- [Extract fields and tables](extraction-platform.md), apply visual templates or generate searchable and redacted outputs.
- [Train a classifier and set QA rules](quality-platform.md), or [group submissions into cases and define flows](flows-platform.md).

## Integrate a client

- [Use the packet API](api.md) for upload, review and export; import the running server’s OpenAPI contract.
- [Consume approved fields](approved-fields.md) from the final export or connector delivery.

## Operate and maintain

- [Configure shared review](operations-platform.md) — identities, workflow access, claims and retention.
- [Inspect durable jobs and replay failures](wolverine.md).
- [Back up and restore](recovery.md); [upgrade an existing installation](renaming.md) without losing its data.

## Develop and evaluate

- [Build, format and run tests](development.md), including isolated package acceptance checks.
- [Prepare and verify a source release](source-release.md) from a reviewed commit.
- [Evaluate real paperwork](evaluation.md) against held-out labels; use the [synthetic benchmark](benchmark.md) for regression checks.
- Consult the [platform verification results](platform-verification.md), [earlier workflow release checks](completion.md) and [requirements and evidence](requirements.md) for the scope of past tests.
