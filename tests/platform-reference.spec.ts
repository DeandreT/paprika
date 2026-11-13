import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

test('real UI dataset and PDF reference lookup survive approval but a corrected reference invalidates export', async ({ page, request }) => {
  test.setTimeout(90_000);
  const suffix = randomUUID().replaceAll('-', '');
  const datasetId = `reference-ui-${suffix}`;
  const reference = `REF-${suffix.slice(0, 20)}`;
  const name = `ARTIFICIAL-reference-${suffix}.pdf`;
  const headers = { 'X-PaperworkClassifier': '1' };

  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'References and connections', exact: true }).click();
  const panel = page.locator('app-reference-panel');
  await panel.getByText('Immutable local reference datasets', { exact: true }).click();
  await panel.getByLabel('Dataset ID', { exact: true }).fill(datasetId);
  await panel.getByLabel('Name', { exact: true }).fill(`ARTIFICIAL reference UI ${suffix}`);
  await panel.getByLabel('Synthetic records (not live business data)', { exact: true }).check();
  await panel.getByLabel('Entries JSON: reference, recordId, label', { exact: true }).fill(JSON.stringify([
    { reference, recordId: `record-${suffix}`, label: 'ARTIFICIAL browser fixture, not a real order' },
  ]));
  const createdResponse = page.waitForResponse(r => r.url().endsWith('/api/references/datasets') && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Create new dataset version', exact: true }).click();
  const created = await createdResponse;
  expect(created.ok(), await created.text()).toBe(true);
  const dataset = await created.json();
  expect(dataset.synthetic).toBe(true);
  expect(dataset.version).toBe(1);
  await expect(panel).toContainText(dataset.hash);

  const catalog = await (await request.get('/api/workflows')).json();
  const baseline = catalog.entries.find((entry: any) => entry.kind === 'workflow' && entry.id === 'general-logistics' && entry.version === 1);
  const draftResponse = await request.post('/api/workflows/catalog', { headers, data: {
    kind: 'workflow', id: `reference-workflow-${suffix}`, name: `ARTIFICIAL reference workflow ${suffix}`, description: 'Synthetic browser acceptance only',
    workflow: { ...baseline.workflow, mode: 'simulation', model: 'local-simulation-1', defaultSplit: { mode: 'separate', pageCount: 1, pattern: '' }, identifierKinds: ['order'], requiredIdentifierKinds: [],
      capabilities: { reference: { mode: 'local', datasetId: dataset.id, datasetVersion: dataset.version, datasetHash: dataset.hash, requireVerified: true, autoVerify: false, identifierKinds: ['order'] } } },
  } });
  expect(draftResponse.ok(), await draftResponse.text()).toBe(true);
  const draft = await draftResponse.json();
  const published = await request.post(`/api/workflows/catalog/workflow/${draft.id}/${draft.version}/publish`, { headers, data: { revision: draft.revision } });
  expect(published.ok(), await published.text()).toBe(true);

  await page.reload();
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('workflow-select').selectOption(`${draft.id}@${draft.version}`);
  const uploadedResponse = page.waitForResponse(r => r.url().endsWith('/api/packets') && r.request().method() === 'POST');
  await page.getByTestId('upload-input').setInputFiles({ name, mimeType: 'application/pdf', buffer: textPdf([`INVOICE\nOrder: ${reference}\nARTIFICIAL acceptance fixture`]) });
  const uploaded = await uploadedResponse;
  expect(uploaded.ok(), await uploaded.text()).toBe(true);
  const queued = await uploaded.json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('review');
  const packet = await (await request.get(`/api/packets/${queued.id}`)).json();
  expect(packet.documents).toHaveLength(1);
  expect(packet.documents[0].businessReference).toBe(reference);
  expect(packet.workflow.capabilities.reference.datasetHash).toBe(dataset.hash);
  expect((await request.get(`/api/packets/${queued.id}/export`)).status()).toBe(409);

  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'References and connections', exact: true }).click();
  await expect(panel).toContainText('candidate');
  await expect(panel).toContainText('SYNTHETIC');
  const verifiedResponse = page.waitForResponse(r => r.url().endsWith(`/api/references/packets/${queued.id}/verify`) && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Verify this match', exact: true }).click();
  const verified = await verifiedResponse;
  expect(verified.ok(), await verified.text()).toBe(true);
  const originalVerification = await verified.json();
  await expect(panel).toContainText('verified');
  await page.getByTestId('approve-review').click();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('approved');
  const approved = await (await request.get(`/api/packets/${queued.id}`)).json();
  const results = await (await request.get(`/api/references/packets/${queued.id}`)).json();
  const carried = results.find((result: any) => result.revision === approved.revision);
  expect(carried.status).toBe('verified');
  expect(carried.resultHash).not.toBe(originalVerification.resultHash);
  expect(carried.synthetic).toBe(true);
  expect(carried.verifiedBy).toBe('local');
  await expect(page.getByTestId('export-packet')).toBeVisible();
  const exportResponse = await request.get(`/api/packets/${queued.id}/export`);
  expect(exportResponse.status()).toBe(200);
  expect(exportResponse.headers()['content-type']).toContain('application/zip');
  expect((await exportResponse.body()).subarray(0, 2).toString()).toBe('PK');

  await page.getByRole('textbox', { name: 'Business reference', exact: true }).fill('EDITED-UNVERIFIED');
  await page.getByTestId('save-review').click();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('review');
  await page.getByTestId('approve-review').click();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status).toBe('approved');
  const changed = await (await request.get(`/api/packets/${queued.id}`)).json();
  const changedResults = await (await request.get(`/api/references/packets/${queued.id}`)).json();
  expect(changedResults.some((result: any) => result.revision === changed.revision && result.status === 'verified')).toBe(false);
  const blocked = await request.get(`/api/packets/${queued.id}/export`);
  expect(blocked.status()).toBe(409);
  expect((await blocked.json()).error).toContain('Verify');
});
