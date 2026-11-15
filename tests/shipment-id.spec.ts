import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

const headers = { 'X-paprika': '1' };

test('guided review asks for a Shipment ID, checks it and blocks approval until every document has one', async ({ page, request }) => {
  const suffix = randomUUID().slice(0, 8);
  const reference = `SYN-${suffix}`;
  const dataset = await (await request.post('/api/references/datasets', { headers, data: {
    id: `shipments-${suffix}`, name: 'SYNTHETIC shipments', synthetic: true,
    entries: [{ reference, recordId: `record-${suffix}`, label: 'Synthetic shipment' }],
  } })).json();
  const catalog = await (await request.get('/api/workflows')).json();
  const baseline = catalog.entries.find((e: any) => e.kind === 'workflow' && e.id === 'general-logistics' && e.version === 1);
  const draft = await (await request.post('/api/workflows/catalog', { headers, data: {
    kind: 'workflow', id: `shipment-${suffix}`, name: `SYNTHETIC shipment workflow ${suffix}`, description: 'Synthetic browser acceptance only',
    workflow: { ...baseline.workflow, mode: 'simulation', model: 'local-simulation-1', identifierKinds: ['shipment'], requiredIdentifierKinds: ['shipment'],
      capabilities: { reference: { mode: 'local', datasetId: dataset.id, datasetVersion: dataset.version, datasetHash: dataset.hash, requireVerified: false, autoVerify: false, identifierKinds: ['shipment'] } } },
  } })).json();
  const published = await request.post(`/api/workflows/catalog/workflow/${draft.id}/${draft.version}/publish`, { headers, data: { revision: draft.revision } });
  expect(published.ok(), await published.text()).toBe(true);

  await page.goto('/');
  await page.getByTestId('workflow-select').selectOption(`${draft.id}@${draft.version}`);
  await page.getByTestId('upload-input').setInputFiles({ name: `synthetic-pod-${suffix}.pdf`, mimeType: 'application/pdf', buffer: textPdf(['PROOF OF DELIVERY\nDelivered and signed\nSYNTHETIC fixture']) });
  const shipment = page.getByLabel('Shipment ID', { exact: true });
  await expect(shipment).toBeVisible();
  await expect(page.getByTestId('shipment-check')).toContainText('Enter the Shipment ID');
  await expect(page.getByTestId('guided-confidence')).toContainText(/Proof of delivery · \d+% rule score/);

  await page.getByTestId('finish-classification-review').click();
  await expect(page.getByTestId('guided-approve')).toBeDisabled();
  await expect(page.getByRole('alert')).toContainText('Add a Shipment ID to one document');
  await page.getByRole('button', { name: 'Back to review', exact: true }).click();

  await shipment.fill(reference);
  await page.getByTestId('check-shipment').click();
  await expect(page.getByTestId('shipment-check')).toContainText('Shipment found: Synthetic shipment');
  await shipment.fill(`MISSING-${suffix}`);
  await page.getByTestId('check-shipment').click();
  await expect(page.getByTestId('shipment-check')).toContainText("wasn't found");
  await shipment.fill(reference);
  await page.getByTestId('finish-classification-review').click();
  await expect(page.getByTestId('guided-approve')).toBeEnabled();
  await page.getByTestId('guided-approve').click();
  await expect(page.getByTestId('classification-step')).toHaveText('Step 4 of 4 · Export');
});
