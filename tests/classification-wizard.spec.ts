import { test, expect } from '@playwright/test';
import { randomUUID, createHash } from 'node:crypto';
import { textPdf } from './fixtures';
import { menuAction } from './navigation';

const headers = { 'X-paprika': '1' };
const fixture = () => textPdf([
  'BILL OF LADING\nOrder: SYNTHETIC-1001\nPage 1 of 2',
  'BILL OF LADING\nOrder: SYNTHETIC-1001\nPage 2 of 2 - continuation',
  'PACKING LIST\nOrder: SYNTHETIC-1001\nPage 1 of 1',
  'INVOICE\nOrder: SYNTHETIC-1002\nPage 1 of 1',
]);

test('guided classification preserves edits across documents and Advanced, then approves and downloads', async ({ page, request }) => {
  const bytes = fixture();
  const name = `synthetic-guided-${randomUUID()}.pdf`;
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/');
  await expect(page.getByTestId('classification-step')).toHaveText('Step 1 of 4 · Upload');
  await expect(page.getByRole('navigation', { name: 'Packets', exact: true })).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Upload packet', exact: true })).toBeEnabled();
  const accepted = page.waitForResponse(r => r.url().endsWith('/api/packets') && r.request().method() === 'POST');
  await page.getByTestId('upload-input').setInputFiles({ name, mimeType: 'application/pdf', buffer: bytes });
  const packet = await (await accepted).json();
  await expect(page.getByTestId('guided-document-type')).toHaveValue('bol');
  await expect(page.getByTestId('guided-document')).toHaveCount(1);
  await expect(page.getByText('Document 1 of 3', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.getByText('Page 2 of 2', { exact: true })).toBeVisible();
  await expect.poll(() => page.locator('.large-preview canvas').evaluate((canvas: HTMLCanvasElement) => canvas.width > 0)).toBe(true);
  await page.getByTestId('guided-document-type').selectOption('pod');
  await page.getByLabel('Business reference', { exact: true }).fill('SYNTHETIC-GUIDED-EDIT');
  await page.getByTestId('next-document').click();
  await expect(page.getByTestId('guided-document-type')).toHaveValue('packing_list');
  await page.getByTestId('previous-document').click();
  await expect(page.getByTestId('guided-document-type')).toHaveValue('pod');
  await expect(page.getByLabel('Business reference', { exact: true })).toHaveValue('SYNTHETIC-GUIDED-EDIT');

  await menuAction(page, 'classification-advanced');
  await menuAction(page, 'classification-guided');
  await expect(page.getByTestId('guided-document-type')).toHaveValue('pod');
  await expect(page.getByLabel('Business reference', { exact: true })).toHaveValue('SYNTHETIC-GUIDED-EDIT');
  await page.getByRole('button', { name: 'Split or reorganize pages', exact: true }).click();
  await expect(page.getByTestId('document-card')).toHaveCount(3);
  await page.getByTestId('page-item').nth(1).click();
  await page.getByRole('combobox', { name: 'Move page to document', exact: true }).selectOption('new');
  await expect(page.getByTestId('document-card')).toHaveCount(4);
  await menuAction(page, 'classification-guided');
  await expect(page.getByText('Document 2 of 4', { exact: true })).toBeVisible();
  await page.getByTestId('previous-document').click();
  await expect(page.getByLabel('Business reference', { exact: true })).toHaveValue('SYNTHETIC-GUIDED-EDIT');
  await page.getByTestId('guided-save').click();
  await expect(page.getByTestId('guided-save')).toBeDisabled();
  for (let i = 0; i < 3; i++) await page.getByTestId('next-document').click();
  await page.getByTestId('finish-classification-review').click();
  await expect(page.getByTestId('classification-step')).toHaveText('Step 3 of 4 · Approve');
  await expect(page.getByTestId('guided-export')).not.toBeVisible();
  await expect(page.locator('.guided-summary li')).toHaveCount(4);
  await page.getByTestId('guided-approve').click();
  await expect(page.getByTestId('classification-step')).toHaveText('Step 4 of 4 · Export');
  const download = page.waitForEvent('download');
  await page.getByTestId('guided-export').click();
  expect((await download).suggestedFilename()).toMatch(/\.zip$/);
  const pdf = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Download document 1', exact: true }).click();
  expect((await pdf).suggestedFilename()).toMatch(/\.pdf$/);
  const approved = await (await request.get(`/api/packets/${packet.id}`)).json();
  expect(approved.status).toBe('approved');
  expect(approved.documents[0]).toMatchObject({ type: 'pod', businessReference: 'SYNTHETIC-GUIDED-EDIT' });
  expect(new Set(approved.documents.flatMap((d: any) => d.pageIds)).size).toBe(4);
  const source = await request.get(approved.pages[0].previewUrl);
  expect(createHash('sha256').update(await source.body()).digest('hex')).toBe(createHash('sha256').update(bytes).digest('hex'));

  await page.getByRole('button', { name: 'Back to review', exact: true }).click();
  await page.getByTestId('guided-document-type').selectOption('receipt');
  await expect(page.getByTestId('guided-export')).not.toBeVisible();
  await page.getByTestId('guided-save').click();
  await expect.poll(async () => (await (await request.get(`/api/packets/${packet.id}`)).json()).status).toBe('review');
  expect((await request.get(`/api/packets/${packet.id}/export`)).ok()).toBe(false);
  await page.setViewportSize({ width: 360, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('guided upload retries keep one submission and rejected approval stays on the confirmation step', async ({ page, request }) => {
  const name = `synthetic-retry-${randomUUID()}.pdf`;
  const keys: string[] = [];
  let id = '';
  await page.route('**/api/packets', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    keys.push(route.request().headers()['idempotency-key']);
    if (keys.length === 1) {
      const response = await route.fetch();
      expect(response.ok()).toBe(true);
      id = (await response.json()).id;
      return route.abort('failed');
    }
    return route.continue();
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Upload packet', exact: true })).toBeEnabled();
  await page.getByTestId('upload-input').setInputFiles({ name, mimeType: 'application/pdf', buffer: textPdf(['INVOICE\nSynthetic guided retry']) });
  await expect(page.getByTestId('upload-retry')).toBeVisible();
  await expect(page.getByTestId('workflow-select')).toBeDisabled();
  await page.getByTestId('upload-retry').click();
  await expect(page.getByTestId('guided-document-type')).toBeVisible();
  expect(keys).toHaveLength(2);
  expect(keys[0]).toBe(keys[1]);
  await page.getByLabel('Business reference', { exact: true }).fill('KEEP-THIS-EDIT');
  await page.getByTestId('finish-classification-review').click();
  await page.route(`**/api/packets/${id}/review`, route => route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ error: 'Required extraction is incomplete.' }) }));
  await page.getByTestId('guided-approve').click();
  await expect(page.getByRole('alert')).toContainText('Required extraction is incomplete');
  await expect(page.getByTestId('classification-step')).toContainText('Approve');
  await expect(page.getByTestId('guided-export')).not.toBeVisible();
  await page.getByRole('button', { name: 'Back to review', exact: true }).click();
  await expect(page.getByLabel('Business reference', { exact: true })).toHaveValue('KEEP-THIS-EDIT');
  await page.unroute(`**/api/packets/${id}/review`);
  await page.getByTestId('finish-classification-review').click();
  await page.getByTestId('guided-approve').click();
  await expect(page.getByTestId('guided-export')).toBeVisible();
  const packets = await (await request.get('/api/packets')).json();
  expect(packets.filter((p: any) => p.name === name)).toHaveLength(1);
  await page.getByRole('button', { name: 'Classify another packet', exact: true }).click();
  await page.getByTestId('existing-packets').click();
  await page.getByLabel('Search packets', { exact: true }).fill(name);
  await page.getByRole('navigation', { name: 'Existing packets', exact: true }).getByRole('button').click();
  await expect(page.getByTestId('classification-step')).toContainText('Export');
});
