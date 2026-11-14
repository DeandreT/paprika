import { test, expect } from '@playwright/test';
import { textPdf } from './fixtures';
import { menuAction } from './navigation';

test('application menu starts closed, dismisses with focus return and remains available in workflow management', async ({ page }) => {
  await page.goto('/');
  const trigger = page.getByTestId('app-menu-toggle');
  const menu = page.getByRole('menu');
  const actions = ['classification-guided', 'classification-advanced', 'workflow-manager-toggle', 'platform-toggle', 'theme-toggle'];
  await expect(trigger).toBeVisible();
  await expect(trigger).toHaveAttribute('aria-expanded', 'false');
  await expect(menu).not.toBeVisible();
  await expect(page.locator('.topbar')).toHaveCount(0);
  for (const action of actions) await expect(page.getByTestId(action)).not.toBeVisible();

  await trigger.focus();
  await page.keyboard.press('Enter');
  await expect(menu).toBeVisible();
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');
  await page.keyboard.press('Escape');
  await expect(menu).not.toBeVisible();
  await expect(trigger).toBeFocused();

  await trigger.click();
  await expect(menu).toBeVisible();
  await page.locator('.cdk-overlay-backdrop').click({ position: { x: 2, y: 2 } });
  await expect(menu).not.toBeVisible();
  await expect(trigger).toBeFocused();

  await menuAction(page, 'classification-advanced');
  await expect(page.locator('.topbar')).toHaveCount(0);
  await expect(page.getByTestId('demo-button')).toBeVisible();
  await expect(page.getByTestId('workflow-select')).toBeVisible();
  await expect(page.getByTestId('upload-input')).toBeAttached();
  await menuAction(page, 'workflow-manager-toggle');
  await expect(page.getByRole('button', { name: 'New workflow', exact: true })).toBeVisible();
  await trigger.click();
  for (const action of actions) await expect(page.getByTestId(action)).toBeEnabled();
  await page.keyboard.press('Escape');
  await expect(trigger).toBeFocused();
  await menuAction(page, 'classification-guided');
  await expect(page.getByTestId('classification-step')).toContainText('Upload');
});

for (const width of [1440, 390, 600]) {
  test(`guided source uses the available width and Fit contains the page at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: width > 600 ? 1000 : 844 });
    await page.goto('/');
    await expect(page.getByRole('button', { name: 'Upload packet', exact: true })).toBeEnabled();
    await page.getByTestId('upload-input').setInputFiles({
      name: `synthetic-document-first-${width}.pdf`,
      mimeType: 'application/pdf',
      buffer: textPdf(['INVOICE\nDocument-first layout\nOrder: SYNTHETIC-LAYOUT']),
    });
    const source = page.getByRole('region', { name: 'Source preview', exact: true });
    const preview = source.locator('.large-preview');
    const canvas = preview.locator('canvas');
    const fields = page.getByTestId('guided-document');
    const details = page.getByTestId('guided-details-toggle');
    await expect(canvas).toBeVisible();
    await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement) => element.width)).toBeGreaterThan(0);
    await expect(canvas).toBeInViewport({ ratio: 0.95 });
    const workspaceBox = (await page.getByRole('main', { name: 'Guided classification', exact: true }).boundingBox())!;
    const sourceBox = (await source.boundingBox())!;
    expect(workspaceBox.width).toBeGreaterThan(width - 80);
    expect(sourceBox.width).toBeGreaterThan(width * (width > 600 ? 0.55 : 0.85));
    if (width > 600) {
      await expect(fields).toBeVisible();
      const fieldsBox = (await fields.boundingBox())!;
      expect(fieldsBox.x).toBeGreaterThanOrEqual(sourceBox.x + sourceBox.width);
      expect(sourceBox.width).toBeGreaterThan(fieldsBox.width);
    } else {
      await expect(details).toHaveAttribute('aria-expanded', 'false');
      await expect(fields).not.toBeVisible();
    }

    const fitBox = (await canvas.boundingBox())!;
    const previewBox = (await preview.boundingBox())!;
    expect(fitBox.x).toBeGreaterThanOrEqual(previewBox.x - 2);
    expect(fitBox.y).toBeGreaterThanOrEqual(previewBox.y - 2);
    expect(fitBox.x + fitBox.width).toBeLessThanOrEqual(previewBox.x + previewBox.width + 2);
    expect(fitBox.y + fitBox.height).toBeLessThanOrEqual(previewBox.y + previewBox.height + 2);
    const status = page.getByTestId('preview-zoom-level');
    await expect(status).toBeVisible();
    expect(await status.evaluate(element => {
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) return element.readOnly;
      return !(element as HTMLElement).isContentEditable && !element.matches('select, button');
    })).toBe(true);
    const fitStatus = await status.textContent();
    expect(fitStatus?.trim()).toBeTruthy();
    await page.getByTestId('preview-zoom-in').click();
    await expect.poll(async () => (await canvas.boundingBox())!.width).toBeGreaterThan(fitBox.width + 1);
    await expect.poll(async () => (await canvas.boundingBox())!.height).toBeGreaterThan(fitBox.height + 1);
    await expect(status).not.toHaveText(fitStatus!);
    const zoomedWidth = (await canvas.boundingBox())!.width;
    await page.getByTestId('preview-zoom-out').click();
    await expect.poll(async () => (await canvas.boundingBox())!.width).toBeLessThan(zoomedWidth - 1);
    await page.getByTestId('preview-zoom-in').click();
    await page.getByTestId('preview-fit').click();
    await expect.poll(async () => Math.abs((await canvas.boundingBox())!.width - fitBox.width)).toBeLessThan(2);
    await expect.poll(async () => Math.abs((await canvas.boundingBox())!.height - fitBox.height)).toBeLessThan(2);
    await expect(status).toHaveText(fitStatus!);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

    if (width <= 600) {
      await details.click();
      await expect(details).toHaveAttribute('aria-expanded', 'true');
      await expect(fields).toBeVisible();
      await page.getByLabel('Business reference', { exact: true }).fill('MOBILE-DETAILS-EDIT');
      await details.click();
      await expect(fields).not.toBeVisible();
      await details.click();
      await expect(page.getByLabel('Business reference', { exact: true })).toHaveValue('MOBILE-DETAILS-EDIT');
    }
  });
}
