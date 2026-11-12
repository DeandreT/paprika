import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { dragPage } from './drag-helpers';

const mutationHeaders = { 'X-PaperworkClassifier': '1' };

test('API preserves all source pages, rejects stale edits, and exports approved revisions', async ({ request }) => {
  const response = await request.post('/api/packets/demo', { headers: mutationHeaders });
  expect(response.ok(), await response.text()).toBeTruthy();
  const created = await response.json();
  let packet = created;
  await expect.poll(async () => {
    packet = await (await request.get(`/api/packets/${created.id}`)).json();
    return packet.status;
  }).toBe('review');
  expect(packet.mode).toBe('demo');
  expect(packet.pages.length).toBeGreaterThanOrEqual(4);
  const originalIds = packet.pages.map((p: { id: string }) => p.id).sort();
  expect(packet.documents.flatMap((d: { pageIds: string[] }) => d.pageIds).sort()).toEqual(originalIds);

  const noApproval = await request.get(`/api/packets/${packet.id}/export`);
  expect(noApproval.ok()).toBeFalsy();

  const missingPage = structuredClone(packet.documents);
  missingPage[0].pageIds.shift();
  const invalid = await request.put(`/api/packets/${packet.id}/review`, {
    headers: mutationHeaders,
    data: { revision: packet.revision, documents: missingPage, approve: true },
  });
  expect(invalid.status()).toBe(400);

  const documents = structuredClone(packet.documents);
  documents[0].businessReference = 'SYNTHETIC-REVIEW-001';
  const saved = await request.put(`/api/packets/${packet.id}/review`, {
    headers: mutationHeaders, data: { revision: packet.revision, documents, approve: false },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  const updated = await saved.json();
  expect(updated.revision).toBeGreaterThan(packet.revision);

  const stale = await request.put(`/api/packets/${packet.id}/review`, {
    headers: mutationHeaders, data: { revision: packet.revision, documents, approve: true },
  });
  expect(stale.status()).toBe(409);

  const approve = await request.put(`/api/packets/${packet.id}/review`, {
    headers: mutationHeaders, data: { revision: updated.revision, documents: updated.documents, approve: true },
  });
  expect(approve.ok(), await approve.text()).toBeTruthy();
  const approved = await approve.json();
  expect(approved.status).toBe('approved');
  const output = await request.get(`/api/packets/${packet.id}/export`);
  expect(output.ok(), await output.text()).toBeTruthy();
  expect(output.headers()['content-type']).toContain('zip');
  expect((await output.body()).subarray(0, 2).toString()).toBe('PK');

  const reopen = await request.put(`/api/packets/${packet.id}/review`, {
    headers: mutationHeaders, data: { revision: approved.revision, documents: approved.documents, approve: false },
  });
  expect(reopen.ok()).toBeTruthy();
  expect((await reopen.json()).status).toBe('review');
  expect((await request.get(`/api/packets/${packet.id}/export`)).ok()).toBeFalsy();
});

test('reviewer can load a synthetic packet, inspect PDF pages, approve and download', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await expect(page.getByRole('heading', { name: 'Documents', level: 1, exact: true })).toBeVisible();
  await page.getByTestId('demo-button').first().click();
  await expect(page.getByTestId('document-card').first()).toBeVisible();
  await expect(page.getByTestId('page-item').first()).toBeVisible();
  await page.getByTestId('page-item').first().click();
  await expect.poll(async () => page.locator('canvas').evaluateAll(canvases =>
    canvases.some(canvas => {
      const c = canvas as HTMLCanvasElement;
      if (!c.width || !c.height) return false;
      const pixels = c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data;
      for (let i = 0; i < pixels.length; i += 4) {
        if (pixels[i + 3] > 0 && pixels[i] < 200 && pixels[i + 1] < 200 && pixels[i + 2] < 200) return true;
      }
      return false;
    })
  )).toBe(true);
  await expect(page.locator('.preview-error')).toHaveCount(0);
  await page.getByTestId('approve-review').click();
  await expect(page.getByTestId('export-packet')).toBeEnabled();
  const downloadEvent = page.waitForEvent('download');
  await page.getByTestId('export-packet').click();
  const download = await downloadEvent;
  expect(download.suggestedFilename()).toMatch(/\.zip$/);
  expect(await download.failure()).toBeNull();
  expect(pageErrors).toEqual([]);
});

test('split, merge, reorder and relabel survive saving; edits invalidate an earlier approval', async ({ page, request }) => {
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('demo-button').first().click();
  await expect(page.getByTestId('document-card')).toHaveCount(4);
  await page.getByTestId('page-item').nth(1).click();
  await page.getByRole('combobox', { name: 'Move page to document', exact: true }).selectOption('new');
  await expect(page.getByTestId('document-card')).toHaveCount(5);
  const firstDestination = await page.getByRole('combobox', { name: 'Move page to document', exact: true }).locator('option').nth(1).getAttribute('value');
  await page.getByRole('combobox', { name: 'Move page to document', exact: true }).selectOption(firstDestination!);
  await expect(page.getByTestId('document-card')).toHaveCount(4);
  await dragPage(page, page.getByTestId('page-item').nth(1), page.getByTestId('page-item').first());
  await expect(page.getByTestId('page-item').first()).toHaveAttribute('aria-label', /source page 2$/);
  await page.getByTestId('document-type').first().selectOption('pod');
  await page.getByLabel('Business reference').first().fill('SYNTHETIC-CORRECTION');
  const savedEvent = page.waitForResponse(response => response.url().endsWith('/review') && response.request().method() === 'PUT');
  await page.getByTestId('save-review').click();
  const saved = await (await savedEvent).json();
  expect(saved.documents[0].type).toBe('pod');
  expect(saved.documents[0].businessReference).toBe('SYNTHETIC-CORRECTION');
  const byId = new Map(saved.pages.map((p: { id: string; pageNumber: number }) => [p.id, p.pageNumber]));
  expect(saved.documents[0].pageIds.map((id: string) => byId.get(id))).toEqual([2, 1]);
  expect(saved.documents.flatMap((d: { pageIds: string[] }) => d.pageIds).length).toBe(5);
  await page.getByTestId('approve-review').click();
  await expect(page.getByTestId('export-packet')).toBeEnabled();
  await page.getByTestId('document-type').first().selectOption('bol');
  await expect(page.getByTestId('export-packet')).toBeDisabled();
  await page.getByTestId('approve-review').click();
  await expect(page.getByTestId('export-packet')).toBeDisabled();
  await expect(page.getByRole('status')).toContainText('approval');
  const latest = await (await request.get(`/api/packets/${saved.id}`)).json();
  expect(latest.status).toBe('review');
});

test('mobile layout does not overflow the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await expect(page.getByTestId('demo-button').first()).toBeVisible();
  await page.getByTestId('demo-button').first().click();
  await expect(page.getByTestId('document-card').first()).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2);
  expect(overflow).toBeFalsy();
});
