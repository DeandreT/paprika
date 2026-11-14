import { test, expect } from '@playwright/test';
import { dragPage } from './drag-helpers';
import { menuAction } from './navigation';

test('drag pages between groups and into a new document, preserving pages and approval rules', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1400 });
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('demo-button').click();
  const cards = page.getByTestId('document-card');
  const pages = page.getByTestId('page-item');
  await expect(cards).toHaveCount(4);
  const originalIds = await pages.evaluateAll(items => items.map(item => item.getAttribute('data-page-id')!));
  await expect(page.getByTestId('split-page')).toHaveCount(0);
  await expect(page.getByTestId('merge-document')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Move earlier|Move later/ })).toHaveCount(0);

  // Cancelling outside a list must neither dirty the packet nor move a page.
  await dragPage(page, pages.first(), page.getByTestId('app-menu-toggle'));
  await expect(page.getByTestId('save-review')).toBeDisabled();
  expect(await pages.evaluateAll(items => items.map(item => item.getAttribute('data-page-id')))).toEqual(originalIds);

  // Moving the only page in a group removes its empty group, not the page.
  const movedId = await cards.nth(1).getByTestId('page-item').getAttribute('data-page-id');
  await dragPage(page, cards.nth(1).getByTestId('page-item'), cards.first().getByTestId('page-item').first());
  await expect(cards).toHaveCount(3);
  await expect(cards.first().getByTestId('page-item')).toHaveCount(3);
  await expect(page.locator(`[data-page-id="${movedId}"]`)).toHaveAttribute('aria-pressed', 'true');

  await dragPage(page, page.locator(`[data-page-id="${movedId}"]`), page.getByTestId('new-document-drop'));
  await expect(cards).toHaveCount(4);
  await expect(cards.nth(1).getByTestId('page-item')).toHaveAttribute('data-page-id', movedId!);
  expect((await pages.evaluateAll(items => items.map(item => item.getAttribute('data-page-id')!))).sort()).toEqual([...originalIds].sort());

  // A held save makes all drag sources and destinations unavailable.
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/packets/*/review', async route => { await gate; await route.continue(); });
  const saving = page.waitForResponse(r => r.url().endsWith('/review') && r.request().method() === 'PUT');
  await page.getByTestId('save-review').click();
  await expect(pages.first()).toHaveClass(/cdk-drag-disabled/);
  await expect(page.getByTestId('new-document-drop')).toHaveClass(/cdk-drop-list-disabled/);
  release();
  const saved = await (await saving).json();
  await page.unroute('**/api/packets/*/review');
  await expect(page.getByTestId('save-review')).toBeDisabled();
  expect(saved.documents.flatMap((d: any) => d.pageIds).sort()).toEqual([...originalIds].sort());
  await page.getByTestId('approve-review').click();
  await expect(page.getByTestId('export-packet')).toBeEnabled();
  await dragPage(page, cards.first().getByTestId('page-item').nth(1), cards.first().getByTestId('page-item').first());
  await expect(page.getByTestId('export-packet')).toBeDisabled();
  await page.getByTestId('save-review').click();
  await expect(page.getByTestId('save-review')).toBeDisabled();
  const latest = await (await request.get(`/api/packets/${saved.id}`)).json();
  expect(latest.status).toBe('review');
  expect(latest.documents[0].pageIds).toEqual([...saved.documents[0].pageIds].reverse());
  expect((await request.get(`/api/packets/${saved.id}/export`)).ok()).toBe(false);
});

test('drag reorders wrapped thumbnails and dropdown movement remains available on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 1050, height: 1000 });
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('demo-button').click();
  const cards = page.getByTestId('document-card');
  await expect(cards).toHaveCount(4);
  const move = page.getByRole('combobox', { name: 'Move page to document', exact: true });
  for (let i = 0; i < 3; i++) {
    await cards.nth(1).getByTestId('page-item').first().click();
    const destination = await move.locator('option').nth(1).getAttribute('value');
    await move.selectOption(destination!);
  }
  await expect(cards).toHaveCount(1);
  const pages = page.getByTestId('page-item');
  await expect(pages).toHaveCount(5);
  const firstBox = (await pages.first().boundingBox())!;
  const lastBox = (await pages.last().boundingBox())!;
  expect(lastBox.y).toBeGreaterThan(firstBox.y);
  const lastId = await pages.last().getAttribute('data-page-id');
  await dragPage(page, pages.last(), pages.first());
  await expect(pages.first()).toHaveAttribute('data-page-id', lastId!);
  await page.getByTestId('save-review').click();
  await expect(page.getByTestId('save-review')).toBeDisabled();
  await page.setViewportSize({ width: 360, height: 844 });
  await move.selectOption('new');
  await expect(cards).toHaveCount(2);
  await expect(cards.nth(1).getByTestId('page-item')).toHaveAttribute('data-page-id', lastId!);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
