import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

test('quality panel trains explicit synthetic data, saves a draft policy and exposes independent QA protection', async ({ page, request }) => {
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
  const examples = Array.from({ length: 10 }, (_, i) => [
    { id: `${suffix}-inv-${i}`, text: `invoice payment due artificialinv${i}`, label: 'invoice', synthetic: true, provenance: 'ARTIFICIAL browser acceptance fixture' },
    { id: `${suffix}-pod-${i}`, text: `delivery signed received artificialpod${i}`, label: 'pod', synthetic: true, provenance: 'ARTIFICIAL browser acceptance fixture' },
  ]).flat();
  await panel.getByRole('textbox', { name: 'Labeled examples JSON' }).fill(JSON.stringify(examples));
  await panel.getByRole('button', { name: 'Import immutable examples', exact: true }).click();
  await expect(panel.getByRole('status')).toContainText('Examples imported');
  const name = `Synthetic UI model ${suffix}`;
  await panel.getByLabel('Name', { exact: true }).fill(name);
  const trainedResponse = page.waitForResponse(r => r.url().endsWith('/api/training/models') && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Train and evaluate', exact: true }).click();
  const trained = await trainedResponse;
  expect(trained.ok(), await trained.text()).toBe(true);
  const model = await trained.json();
  expect(model.artifact.synthetic).toBe(true);
  const card = panel.locator('section').filter({ hasText: name });
  await expect(card).toContainText('Synthetic training');
  await expect(card.getByRole('button', { name: 'Approve real evaluated artifact' })).toBeDisabled();
  await panel.getByRole('combobox', { name: 'Workflow draft', exact: true }).selectOption(`${draft.id}:${draft.version}`);
  await panel.getByRole('combobox', { name: 'Trained artifact', exact: true }).selectOption(model.id);
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
  const published = await request.post(`/api/workflows/catalog/workflow/${saved.id}/${saved.version}/publish`, { headers, data: { revision: saved.revision } });
  expect(published.ok(), await published.text()).toBe(true);
  const uploaded = await request.post('/api/packets', { headers, multipart: {
    files: { name: `ARTIFICIAL-quality-ui-${suffix}.pdf`, mimeType: 'application/pdf', buffer: textPdf(['invoice payment due']) }, workflowId: saved.id, workflowVersion: String(saved.version),
  } });
  expect(uploaded.ok(), await uploaded.text()).toBe(true);
  const queued = await uploaded.json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('review');
  const packet = await (await request.get(`/api/packets/${queued.id}`)).json();
  expect(packet.workflow.capabilities.training.hash).toBe(model.hash);
  expect(packet.workflow.capabilities.quality.autoApprove).toBe(false);
  const approvedResponse = await request.put(`/api/packets/${packet.id}/review`, { headers, data: { revision: packet.revision, documents: packet.documents, approve: true } });
  expect(approvedResponse.ok(), await approvedResponse.text()).toBe(true);
  const approved = await approvedResponse.json();
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
