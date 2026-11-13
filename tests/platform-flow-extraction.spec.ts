import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

test('structured extraction and flow editors produce an approval-gated durable output', async ({ page, request }) => {
  test.setTimeout(120_000);
  const suffix = randomUUID().replaceAll('-', '');
  const definitionId = `flow-fields-${suffix}`;
  const flowId = `flow-ui-${suffix}`;
  const workflowId = `flow-workflow-${suffix}`;
  const reference = `FLOW-${suffix.slice(0, 16).toUpperCase()}`;

  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Extraction and images', exact: true }).click();
  const extractionPanel = page.locator('app-extraction-panel');
  await extractionPanel.getByText('Extraction, visual templates, image processing and outputs', { exact: true }).click();
  await extractionPanel.getByLabel('Definition ID', { exact: true }).fill(definitionId);
  await extractionPanel.getByLabel('Definition version', { exact: true }).fill('1');
  await extractionPanel.getByLabel('Document type (empty applies to all)', { exact: true }).fill('invoice');
  const field = extractionPanel.getByRole('group', { name: 'Field 1', exact: true });
  await field.getByLabel('Name', { exact: true }).fill('orderRef');
  await field.getByRole('combobox', { name: 'Match method', exact: true }).selectOption('label');
  await field.getByLabel('Label', { exact: true }).fill('Order');
  await field.getByLabel('Required', { exact: true }).check();
  await extractionPanel.getByRole('group', { name: 'Field 2', exact: true }).getByRole('button', { name: 'Remove field', exact: true }).click();
  await extractionPanel.getByRole('group', { name: 'Table 1', exact: true }).getByRole('button', { name: 'Remove table', exact: true }).click();
  const definitionSaved = page.waitForResponse(r => r.url().endsWith('/api/extraction/definitions') && r.request().method() === 'POST');
  await extractionPanel.getByRole('button', { name: 'Save new immutable version', exact: true }).click();
  const definitionResponse = await definitionSaved;
  expect(definitionResponse.ok(), await definitionResponse.text()).toBe(true);
  const definition = await definitionResponse.json();
  expect(definition.definition.fields).toHaveLength(1);
  expect(definition.definition.fields[0]).toMatchObject({ name: 'orderRef', label: 'Order', required: true });
  await expect(extractionPanel.getByRole('status')).toContainText('Immutable definition version saved');

  await page.getByRole('button', { name: 'Processing flows', exact: true }).click();
  const flowPanel = page.locator('app-flow-panel');
  await flowPanel.getByRole('button', { name: 'Manage reusable definitions (administrator)', exact: true }).click();
  await flowPanel.getByLabel('Flow identity', { exact: true }).fill(flowId);
  await flowPanel.getByLabel('Name', { exact: true }).fill(`ARTIFICIAL processing flow ${suffix}`);
  async function addBlock(kind: string, id: string, dependency?: string) {
    await flowPanel.getByRole('combobox', { name: 'New block kind', exact: true }).selectOption(kind);
    const index = await flowPanel.locator('fieldset').count();
    await flowPanel.getByRole('button', { name: 'Add block', exact: true }).click();
    await expect(flowPanel.locator('fieldset')).toHaveCount(index + 1);
    const block = flowPanel.locator('fieldset').nth(index);
    await block.getByLabel('Block identity', { exact: true }).fill(id);
    if (dependency) await block.getByRole('checkbox', { name: dependency, exact: true }).check();
    return block;
  }
  await addBlock('Extraction', 'extract');
  const normalization = await addBlock('Transform', 'normalize', 'extract');
  await normalization.getByRole('combobox', { name: 'Operation', exact: true }).selectOption('Uppercase');
  await normalization.getByLabel('Source field', { exact: true }).fill('orderRef');
  await normalization.getByLabel('Target field', { exact: true }).fill('normalized');
  await addBlock('Case', 'case', 'normalize');
  const outputBlock = await addBlock('Output', 'output', 'case');
  await outputBlock.getByRole('combobox', { name: 'Run condition', exact: true }).selectOption('FieldEquals');
  await outputBlock.getByLabel('Document type / field / verified reference', { exact: true }).fill('normalized');
  await outputBlock.getByLabel('Equals', { exact: true }).fill(reference);
  const flowSaved = page.waitForResponse(r => r.url().endsWith(`/api/flows/library/${flowId}`) && r.request().method() === 'PUT');
  await flowPanel.getByRole('button', { name: 'Validate and save', exact: true }).click();
  const flowResponse = await flowSaved;
  expect(flowResponse.ok(), await flowResponse.text()).toBe(true);
  const flow = await flowResponse.json();
  expect(flow.definition.blocks.map((block: any) => block.kind)).toEqual(['Extraction', 'Transform', 'Case', 'Output']);
  await expect(flowPanel.getByRole('status')).toContainText('Reusable flow saved');

  // Select the actual saved objects in the draft editor rather than injecting raw capability JSON.
  await menuAction(page, 'workflow-manager-toggle');
  const manager = page.locator('app-workflow-manager');
  await manager.getByRole('button', { name: 'Workflows', exact: true }).click();
  await manager.getByRole('button', { name: 'New workflow', exact: true }).click();
  await manager.getByTestId('catalog-name').fill(`ARTIFICIAL flow workflow ${suffix}`);
  await manager.getByTestId('workflow-advanced').click();
  await manager.getByLabel('Stable ID', { exact: true }).fill(workflowId);
  await manager.getByLabel('Description (optional)', { exact: true }).fill('Synthetic browser extraction and approval-continuation acceptance');
  await manager.getByRole('combobox', { name: 'Document type release', exact: true }).selectOption('logistics@1');
  await manager.getByRole('combobox', { name: 'Default split policy', exact: true }).selectOption('separate');
  const capabilities = manager.getByTestId('capability-editor');
  await capabilities.getByRole('button', { name: 'Load capability definitions', exact: true }).click();
  await capabilities.getByRole('checkbox', { name: 'Extract fields and tables', exact: true }).check();
  await expect(capabilities.getByTestId('capability-extraction').locator('option', { hasText: definitionId })).toHaveCount(1);
  await capabilities.getByTestId('capability-extraction').selectOption(`${definitionId}@1`);
  await capabilities.getByRole('checkbox', { name: 'Group submissions into cases', exact: true }).check();
  await capabilities.getByLabel('Customer scope', { exact: true }).fill(`artificial-${suffix}`);
  await capabilities.getByLabel('Expected document types (comma-separated)', { exact: true }).fill('invoice, pod');
  await capabilities.getByRole('checkbox', { name: 'Use a composable processing flow', exact: true }).check();
  await expect(capabilities.getByTestId('capability-flow').locator(`option[value="${flowId}"]`)).toHaveCount(1);
  await capabilities.getByTestId('capability-flow').selectOption(flowId);
  await capabilities.getByRole('checkbox', { name: 'Generate additional approved output', exact: true }).check();
  await capabilities.getByTestId('capability-output').selectOption('text-json');
  const draftSaved = page.waitForResponse(r => r.url().endsWith('/api/workflows/catalog') && r.request().method() === 'POST');
  await manager.getByTestId('catalog-save').click();
  const draftResponse = await draftSaved;
  expect(draftResponse.ok(), await draftResponse.text()).toBe(true);
  const draft = await draftResponse.json();
  expect(draft.workflow.capabilities.extraction.sha256).toBe(definition.sha256);
  expect(draft.workflow.capabilities.flow.definition).toEqual(flow.definition);
  const publication = page.waitForResponse(r => r.url().endsWith(`/api/workflows/catalog/workflow/${workflowId}/1/publish`) && r.request().method() === 'POST');
  await manager.getByTestId('catalog-publish').click();
  const publishedResponse = await publication;
  expect(publishedResponse.ok(), await publishedResponse.text()).toBe(true);

  await page.reload();
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('workflow-select').selectOption(`${workflowId}@1`);
  const uploadedResponse = page.waitForResponse(r => r.url().endsWith('/api/packets') && r.request().method() === 'POST');
  await page.getByTestId('upload-input').setInputFiles({
    name: `ARTIFICIAL-flow-${suffix}.pdf`, mimeType: 'application/pdf',
    buffer: textPdf([`INVOICE\nOrder: ${reference}\nARTIFICIAL flow acceptance fixture`]),
  });
  const uploaded = await uploadedResponse;
  expect(uploaded.ok(), await uploaded.text()).toBe(true);
  const queued = await uploaded.json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status, { timeout: 30_000 }).toBe('review');
  const packet = await (await request.get(`/api/packets/${queued.id}`)).json();
  expect(packet.workflow.capabilities.extraction.sha256).toBe(definition.sha256);
  expect(packet.documents[0]).toMatchObject({ type: 'invoice', businessReference: '' });
  const before = await (await request.get(`/api/flows/${queued.id}`)).json();
  expect(before.executions.find((block: any) => block.blockId === 'normalize').result.fields.normalized).toBe(reference);
  expect(before.executions.find((block: any) => block.blockId === 'output').status).toBe('waitingApproval');
  expect(await (await request.get(`/api/outputs/${queued.id}`)).json()).toEqual([]);
  const extraction = await (await request.get(`/api/extraction/${queued.id}`)).json();
  const extracted = extraction.runs.find((run: any) => run.packetRevision === packet.revision).results[0].fields[0];
  expect(extracted).toMatchObject({ name: 'orderRef', value: reference, status: 'extracted' });
  expect(extracted.evidence.length).toBeGreaterThan(0);

  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Cases', exact: true }).click();
  const casePanel = page.locator('app-case-panel');
  await expect(casePanel.getByRole('heading', { name: 'Cross-submission cases', exact: true })).toBeVisible();
  await expect(casePanel).toContainText('Membership proposal');
  await page.getByRole('button', { name: 'Processing flows', exact: true }).click();
  await expect(flowPanel).toContainText('waitingApproval');
  await page.getByTestId('approve-review').click();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('approved');
  const approved = await (await request.get(`/api/packets/${queued.id}`)).json();
  await expect.poll(async () => (await (await request.get(`/api/outputs/${queued.id}`)).json()).length, { timeout: 30_000 }).toBe(1);
  const artifacts = await (await request.get(`/api/outputs/${queued.id}`)).json();
  expect(artifacts[0]).toMatchObject({ revision: approved.revision, format: 'text-json', preview: false });
  const artifactResponse = await request.get(`/api/outputs/${queued.id}/files/${artifacts[0].id}`);
  expect(artifactResponse.ok(), await artifactResponse.text()).toBe(true);
  const artifact = await artifactResponse.json();
  expect(artifact).toMatchObject({ packetId: queued.id, revision: approved.revision });
  expect(artifact.pages[0].text).toContain(reference);

  // A fresh UI fetch reads the persisted current-revision ledger, not an in-memory success message.
  await flowPanel.getByRole('button', { name: 'Refresh execution', exact: true }).click();
  await expect(flowPanel).toContainText('completed');
  await expect.poll(async () => {
    const current = await (await request.get(`/api/flows/${queued.id}`)).json();
    return current.executions.find((block: any) => block.blockId === 'output')?.status;
  }).toBe('completed');
  const finished = await (await request.get(`/api/flows/${queued.id}`)).json();
  expect(finished.packetRevision).toBe(approved.revision);
  expect(finished.executions.find((block: any) => block.blockId === 'output').status).toBe('completed');
  await page.reload();
  await menuAction(page, 'classification-advanced');
  const persisted = await (await request.get(`/api/outputs/${queued.id}`)).json();
  expect(persisted.map((output: any) => output.id)).toEqual([artifacts[0].id]);
});
