import { test, expect, type Locator, type Page } from '@playwright/test';
import { menuAction } from './navigation';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

async function createDataset(page: Page, panel: Locator, name: string): Promise<string> {
  await panel.getByRole('textbox', { name: 'New dataset name' }).fill(name);
  const created = page.waitForResponse(r => r.url().endsWith('/api/training/datasets') && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Create dataset', exact: true }).click();
  const response = await created;
  expect(response.ok(), await response.text()).toBe(true);
  const dataset = await response.json();
  expect(dataset.name).toBe(name);
  await expect(panel.getByRole('combobox', { name: 'Training dataset', exact: true })).toHaveValue(dataset.id);
  return dataset.id;
}

test('quality panel isolates named datasets, freezes model provenance and saves review collection independently', async ({ page, request }) => {
  test.setTimeout(90_000);
  const suffix = randomUUID().replaceAll('-', '');
  const headers = { 'X-paprika': '1' };
  const catalog = await (await request.get('/api/workflows')).json();
  const baseline = catalog.entries.find((e: any) => e.kind === 'workflow' && e.id === 'general-logistics' && e.version === 1);
  const draftResponse = await request.post('/api/workflows/catalog', { headers, data: {
    kind: 'workflow', id: `quality-ui-${suffix}`, name: `Quality UI ${suffix}`, description: 'Synthetic acceptance fixture only',
    workflow: { ...baseline.workflow, mode: 'simulation', model: 'local-simulation-1', defaultSplit: { mode: 'separate', pageCount: 1, pattern: '' }, capabilities: null },
  } });
  expect(draftResponse.ok(), await draftResponse.text()).toBe(true);
  const draft = await draftResponse.json();
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Quality and training', exact: true }).click();
  const panel = page.locator('app-quality-panel');
  await panel.getByText('Quality, independent QA and trained text models', { exact: true }).click();
  const datasetPicker = panel.getByRole('combobox', { name: 'Training dataset', exact: true });
  await expect(datasetPicker).toHaveValue('');
  await expect(panel.getByRole('button', { name: 'Train and evaluate' })).toHaveCount(0);
  const invoiceName = `Synthetic invoice desk ${suffix.slice(0, 8)}`;
  const manifestName = `Synthetic manifest desk ${suffix.slice(0, 8)}`;
  const manifestSet = await createDataset(page, panel, manifestName);
  const manifest = { id: 'manifest-01', text: 'Artificial dock manifest sample.', label: 'packing_list', synthetic: true, provenance: 'Synthetic manifest desk' };
  const importBox = panel.getByRole('textbox', { name: 'Labeled examples JSON' });
  const importButton = panel.getByRole('button', { name: 'Import immutable examples', exact: true });
  await importBox.fill(JSON.stringify([manifest]));
  await importButton.click();
  await expect(panel.getByRole('status')).toContainText('Examples imported');
  const invoiceSet = await createDataset(page, panel, invoiceName);
  const examples = Array.from({ length: 10 }, (_, i) => [
    { id: `invoice-${i}`, text: `invoice payment due artificialinv${i}`, label: 'invoice', synthetic: true, provenance: 'Synthetic invoice desk' },
    { id: `delivery-${i}`, text: `delivery signed received artificialpod${i}`, label: 'pod', synthetic: true, provenance: 'Synthetic invoice desk' },
  ]).flat();
  await importBox.fill(JSON.stringify(examples));
  await importButton.click();
  await expect(panel.getByRole('status')).toContainText('Examples imported');
  await datasetPicker.selectOption(manifestSet);
  await panel.getByRole('button', { name: 'List examples', exact: true }).click();
  const manifestTable = panel.getByRole('table', { name: new RegExp(manifestName) });
  await expect(manifestTable.getByRole('row', { name: 'manifest-01 packing_list Synthetic' })).toBeVisible();
  await expect(manifestTable).not.toContainText(examples[0].id);
  await expect(manifestTable).not.toContainText(manifest.text);
  await datasetPicker.selectOption(invoiceSet);
  await expect(manifestTable).toHaveCount(0);
  await panel.getByRole('button', { name: 'List examples', exact: true }).click();
  const invoiceTable = panel.getByRole('table', { name: new RegExp(invoiceName) });
  await expect(invoiceTable.getByRole('cell', { name: examples[0].id, exact: true })).toBeVisible();
  await expect(invoiceTable).not.toContainText(manifest.id);
  const name = `Synthetic invoice classifier ${suffix.slice(0, 8)}`;
  await panel.getByLabel('Name', { exact: true }).fill(name);
  const trainedResponse = page.waitForResponse(r => r.url().endsWith(`/api/training/datasets/${invoiceSet}/models`) && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Train and evaluate', exact: true }).click();
  const trained = await trainedResponse;
  expect(trained.ok(), await trained.text()).toBe(true);
  const model = await trained.json();
  expect(model.artifact.synthetic).toBe(true);
  expect(model.artifact.dataset).toEqual({ id: invoiceSet, name: invoiceName });
  expect(model.artifact.datasetExampleCount).toBe(examples.length);
  expect(model.artifact.labels).toEqual(['invoice', 'pod']);
  const card = panel.locator('section').filter({ hasText: name });
  await expect(card).toContainText('Synthetic training');
  await expect(card).toContainText(invoiceName);
  await expect(card.getByRole('button', { name: 'Approve real evaluated artifact' })).toBeDisabled();
  const renamed = `Renamed synthetic invoice desk ${suffix.slice(0, 8)}`;
  await panel.getByText('Rename dataset', { exact: true }).click();
  await panel.getByLabel('Updated dataset name').fill(renamed);
  await panel.getByRole('button', { name: 'Save dataset name' }).click();
  await expect(datasetPicker.locator('option:checked')).toContainText(renamed);
  await expect(card).toContainText(invoiceName);
  await expect(card).not.toContainText(renamed);
  await panel.getByRole('combobox', { name: 'Workflow draft', exact: true }).selectOption(`${draft.id}:${draft.version}`);
  await panel.getByRole('combobox', { name: 'Trained artifact', exact: true }).selectOption(model.id);
  const reviewPicker = panel.getByRole('combobox', { name: 'Review collection dataset', exact: true });
  await expect(reviewPicker.locator('option:checked')).toContainText('do not collect');
  await reviewPicker.selectOption(manifestSet);
  await panel.getByRole('combobox', { name: 'Review mode', exact: true }).selectOption('selective');
  await panel.getByLabel('Enable automatic approval', { exact: true }).uncheck();
  await panel.getByLabel('Routing score threshold', { exact: true }).fill('0.95');
  await panel.getByLabel('Independent QA sample rate (0–1)', { exact: true }).fill('1');
  await panel.getByLabel('QA sampling seed', { exact: true }).fill(suffix);
  await panel.getByLabel('Minimum held-out examples', { exact: true }).fill('50');
  await panel.getByLabel('Maximum held-out error upper bound', { exact: true }).fill('0.1');
  await panel.getByLabel('Synthetic evaluation only', { exact: true }).check();
  await panel.getByLabel('Require independent QA reviewer', { exact: true }).check();
  await panel.getByRole('button', { name: 'Validate and save draft quality/training settings' }).click();
  await expect(panel.getByRole('status')).toContainText('Workflow draft saved');
  const updatedCatalog = await (await request.get('/api/workflows')).json();
  const saved = updatedCatalog.entries.find((e: any) => e.kind === 'workflow' && e.id === draft.id && e.version === draft.version);
  expect(saved.workflow.capabilities.training.hash).toBe(model.hash);
  expect(saved.workflow.capabilities.training.approved).toBe(false);
  expect(JSON.parse(saved.workflow.capabilities.training.payload).dataset).toEqual({ id: invoiceSet, name: invoiceName });
  expect(saved.workflow.capabilities.reviewCollection.datasetId).toBe(manifestSet);
  const uploaded = await request.post('/api/packets', { headers, multipart: {
    files: { name: `ARTIFICIAL-quality-ui-${suffix}.pdf`, mimeType: 'application/pdf', buffer: textPdf(['invoice payment due']) },
    workflowId: saved.id, workflowVersion: String(saved.version), workflowRevision: String(saved.revision), testRun: 'true',
  } });
  expect(uploaded.ok(), await uploaded.text()).toBe(true);
  const queued = await uploaded.json();
  const published = await request.post(`/api/workflows/catalog/workflow/${saved.id}/${saved.version}/publish`, { headers, data: { revision: saved.revision } });
  expect(published.ok(), await published.text()).toBe(true);
  expect((await published.json()).workflow.capabilities.reviewCollection.datasetId).toBe(manifestSet);
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('review');
  const packet = await (await request.get(`/api/packets/${queued.id}`)).json();
  expect(packet.testRun).toBe(true);
  expect(packet.workflow.capabilities.training.hash).toBe(model.hash);
  expect(packet.workflow.capabilities.reviewCollection.datasetId).toBe(manifestSet);
  expect(packet.workflow.capabilities.quality.autoApprove).toBe(false);
  const approvedResponse = await request.put(`/api/packets/${packet.id}/review`, { headers, data: { revision: packet.revision, documents: packet.documents, approve: true } });
  expect(approvedResponse.ok(), await approvedResponse.text()).toBe(true);
  const approved = await approvedResponse.json();
  const reviewExamples = await (await request.get(`/api/training/datasets/${manifestSet}/examples`)).json();
  expect(reviewExamples).toHaveLength(2);
  expect(reviewExamples).toEqual(expect.arrayContaining([
    expect.objectContaining({ packetId: packet.id, revision: approved.revision, synthetic: true, label: 'invoice' }),
  ]));
  const trainingExamples = await (await request.get(`/api/training/datasets/${invoiceSet}/examples`)).json();
  expect(trainingExamples).toHaveLength(examples.length);
  const quality = await (await request.get(`/api/quality/packets/${packet.id}`)).json();
  expect(quality.tasks).toHaveLength(1);
  expect(quality.tasks[0].packetRevision).toBe(approved.revision);
  expect(quality.tasks[0].snapshot.status).toBe('approved');

  await page.reload();
  await menuAction(page, 'classification-advanced');
  // Select the actual submitted packet through the inbox, not a mocked panel input.
  await page.getByRole('navigation', { name: 'Packets', exact: true }).getByRole('button').filter({ hasText: `ARTIFICIAL-quality-ui-${suffix}.pdf` }).click();
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Quality and training', exact: true }).click();
  const selectedPanel = page.locator('app-quality-panel');
  await selectedPanel.getByText('Quality, independent QA and trained text models', { exact: true }).click();
  await expect(selectedPanel).toContainText(`Packet revision ${approved.revision}`);
  await selectedPanel.getByRole('button', { name: 'Inspect frozen QA snapshot' }).click();
  await expect(selectedPanel.locator('pre')).toContainText('"status": "approved"');
  // Local mode has one actor: primary review cannot be passed off as independent QA.
  await selectedPanel.getByRole('button', { name: 'QA pass', exact: true }).click();
  await expect(selectedPanel.getByRole('alert')).toContainText('independent reviewer');
  const unchanged = await (await request.get(`/api/quality/packets/${packet.id}`)).json();
  expect(unchanged.tasks[0].status).toBe('pending');
  expect((await (await request.get(`/api/packets/${packet.id}`)).json()).revision).toBe(approved.revision);
});
