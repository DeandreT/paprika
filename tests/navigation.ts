import { expect, type Page } from '@playwright/test';

export async function menuAction(page: Page, testId: string) {
  await page.getByTestId('app-menu-toggle').click();
  await page.getByTestId(testId).click();
  await expect(page.getByRole('menu')).not.toBeVisible();
}
