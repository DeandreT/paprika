import { expect, Locator, Page } from '@playwright/test';

export async function dragPage(page: Page, source: Locator, target: Locator) {
  await source.scrollIntoViewIfNeeded();
  const start = (await source.boundingBox())!;
  await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
  await page.mouse.down();
  await page.mouse.move(start.x + start.width / 2 + 10, start.y + start.height / 2, { steps: 3 });
  await expect(page.locator('.cdk-drag-preview')).toBeVisible();
  await target.scrollIntoViewIfNeeded();
  const end = (await target.boundingBox())!;
  await page.mouse.move(end.x + end.width / 4, end.y + end.height / 2, { steps: 15 });
  await page.mouse.up();
  await expect(page.locator('.cdk-drag-preview')).toHaveCount(0);
}
