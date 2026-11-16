import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';

test('page title and application menu use lowercase paprika branding', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle('paprika');
  await page.getByTestId('app-menu-toggle').click();
  await expect(page.getByRole('menu').locator('.menu-brand')).toHaveText('paprika');
});

for (const legacyTheme of ['dark', 'light'] as const) {
  const otherTheme = legacyTheme === 'dark' ? 'light' : 'dark';

  test(`theme bootstrap respects legacy ${legacyTheme} preference before Angular starts`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: otherTheme });
    await page.addInitScript(theme => localStorage.setItem('paperwork-theme', theme), legacyTheme);
    await page.route('**/*', route => route.request().resourceType() === 'script' ? route.abort() : route.continue());
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-theme', legacyTheme);
  });

  test(`theme service preserves legacy ${legacyTheme} preference and saves toggles under paprika`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: otherTheme });
    await page.addInitScript(theme => localStorage.setItem('paperwork-theme', theme), legacyTheme);
    await page.goto('/');
    await expect(page.getByTestId('app-menu-toggle')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', legacyTheme);
    await menuAction(page, 'theme-toggle');
    await expect(page.locator('html')).toHaveAttribute('data-theme', otherTheme);
    expect(await page.evaluate(() => localStorage.getItem('paprika-theme'))).toBe(otherTheme);
    expect(await page.evaluate(() => localStorage.getItem('paperwork-theme'))).toBe(legacyTheme);
    await page.reload();
    await expect(page.getByTestId('app-menu-toggle')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', otherTheme);
  });

  test(`theme bootstrap prefers paprika ${otherTheme} over legacy ${legacyTheme}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: legacyTheme });
    await page.addInitScript(({ legacy, current }) => {
      localStorage.setItem('paperwork-theme', legacy);
      localStorage.setItem('paprika-theme', current);
    }, { legacy: legacyTheme, current: otherTheme });
    await page.route('**/*', route => route.request().resourceType() === 'script' ? route.abort() : route.continue());
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-theme', otherTheme);
  });
}
