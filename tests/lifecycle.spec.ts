import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { textPdf } from './fixtures';

const headers = { 'X-paprika': '1' };
const fixture = () => textPdf([
  'SYNTHETIC TEST ONLY\nBILL OF LADING\nLOAD #: LOAD-001\nPage 1 of 2',
  'SYNTHETIC TEST ONLY\nBILL OF LADING\nLOAD #: LOAD-001\nPage 2 of 2 - continuation',
  'SYNTHETIC TEST ONLY\nPACKING LIST\nLOAD #: LOAD-001\nPage 1 of 1',
  'SYNTHETIC TEST ONLY\nINVOICE\nORDER #: ORDER-002\nPage 1 of 1',
]);

test('uncertain upload retry uses the same idempotency key and returns the original submission', async ({ page, request }) => {
  let firstId = '';
  const keys: string[] = [];
  await page.route('**/api/packets', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    keys.push(route.request().headers()['idempotency-key']);
    if (keys.length === 1) {
      const accepted = await route.fetch();
      expect(accepted.ok()).toBeTruthy();
      firstId = (await accepted.json()).id;
      // Simulate losing the HTTP response AFTER durable acceptance.
      await route.abort('failed');
    } else await route.continue();
  });
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await expect(page.getByRole('button', { name: /upload packet/i }).first()).toBeEnabled();
  await page.getByTestId('upload-input').setInputFiles({ name: 'retry-safe.pdf', mimeType: 'application/pdf', buffer: fixture() });
  await expect(page.getByTestId('upload-retry')).toBeVisible();
  await page.getByTestId('upload-retry').click();
  await expect(page.getByTestId('document-card').first()).toBeVisible();
  expect(keys).toHaveLength(2);
  expect(keys[0]).toBeTruthy();
  expect(keys[1]).toBe(keys[0]);
  const packets = await (await request.get('/api/packets')).json();
  expect(packets.filter((p: { name: string }) => p.name === 'retry-safe.pdf')).toHaveLength(1);
  expect(packets.find((p: { name: string }) => p.name === 'retry-safe.pdf').id).toBe(firstId);
});

test('reference evidence, simulated delivery retries, and a new processing run preserve approved work', async ({ page, request }) => {
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await expect(page.getByRole('button', { name: /upload packet/i }).first()).toBeEnabled();
  const uploadEvent = page.waitForResponse(r => r.url().endsWith('/api/packets') && r.request().method() === 'POST');
  await page.getByTestId('upload-input').setInputFiles({ name: 'lifecycle.pdf', mimeType: 'application/pdf', buffer: fixture() });
  const uploaded = await (await uploadEvent).json();
  await expect(page.getByTestId('document-card')).toHaveCount(3);
  const initial = await (await request.get(`/api/packets/${uploaded.id}`)).json();
  expect(initial.identifierCandidates.some((c: { value: string }) => c.value === 'LOAD-001')).toBe(true);
  await page.getByTestId('page-item').first().click();
  await page.getByTestId('use-reference').first().click();
  await expect(page.getByLabel('Business reference').first()).toHaveValue('LOAD-001');
  await page.getByTestId('page-item').nth(2).click();
  await page.getByTestId('use-reference').first().click();
  await expect(page.getByLabel('Business reference').nth(1)).toHaveValue('LOAD-001');
  await page.getByTestId('approve-review').click();
  await expect(page.getByTestId('export-packet')).toBeEnabled();
  await page.getByTestId('delivery-scenario').selectOption('transient-once');
  await page.getByTestId('queue-delivery').click();
  // Wolverine retries the transient transport failure without operator action.
  await expect(page.getByTestId('delivery-item').first()).toContainText('delivered');
  const deliveries = await (await request.get(`/api/packets/${uploaded.id}/deliveries`)).json();
  expect(deliveries).toHaveLength(1);
  expect(deliveries[0].mode).toBe('simulation');
  expect(deliveries[0].attempts).toBe(2);
  expect(deliveries[0].sha256).toMatch(/^[a-f0-9]{64}$/i);
  const duplicate = await request.post(`/api/packets/${uploaded.id}/deliveries`, {
    headers, data: { revision: deliveries[0].revision, scenario: 'transient-once' },
  });
  expect(duplicate.ok()).toBeTruthy();
  expect((await duplicate.json()).id).toBe(deliveries[0].id);
  const downloadEvent = page.waitForEvent('download');
  await page.getByTestId('delivery-download').first().click();
  expect((await downloadEvent).suggestedFilename()).toMatch(/\.zip$/i);
  await page.getByTestId('operations-toggle').click();
  const ops = await (await request.get('/api/operations')).json();
  expect(ops.deliveries.delivered).toBeGreaterThanOrEqual(1);
  const old = await (await request.get(`/api/packets/${uploaded.id}`)).json();
  const reprocessEvent = page.waitForResponse(r => r.url().endsWith('/reprocess') && r.request().method() === 'POST');
  await page.getByTestId('reprocess-packet').click();
  const rerun = await (await reprocessEvent).json();
  expect(rerun.id).not.toBe(uploaded.id);
  expect(rerun.parentPacketId).toBe(uploaded.id);
  expect(rerun.runNumber).toBe(2);
  await expect(page.getByTestId('document-card')).toHaveCount(3);
  const original = await (await request.get(`/api/packets/${uploaded.id}`)).json();
  expect(original.status).toBe('approved');
  expect(original.revision).toBe(old.revision);
  expect(original.documents).toEqual(old.documents);
  expect((await request.get(deliveries[0].artifactUrl)).ok()).toBeTruthy();
});
