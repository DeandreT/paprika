import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { randomUUID } from 'node:crypto';

test('selected split policies explain their behavior without showing unrelated help', async ({ page }) => {
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'New workflow', exact: true }).click();
  await page.getByTestId('catalog-name').fill('Synthetic split-policy guidance');
  await page.getByTestId('workflow-next').click();
  await page.getByTestId('workflow-next').click();
  const explanations = {
    classifier: 'Start a document where the classifier predicts',
    separate: 'Make every page its own document.',
    consecutive: 'Group adjacent pages with the same predicted type.',
    manual: 'Start with one document per page',
    fixed: 'Group a fixed number of consecutive pages',
    first: 'first-page pattern',
    last: 'last-page pattern',
    identifier: 'captured identifier changes',
  };
  for (const [value, text] of Object.entries(explanations)) {
    await page.getByRole('combobox', { name: 'Default split policy', exact: true }).selectOption(value);
    await expect(page.getByTestId('split-explanation')).toHaveCount(1);
    await expect(page.getByTestId('split-explanation')).toContainText(text);
    await expect(page.getByTestId('split-explanation')).toContainText('Example:');
  }
  await page.getByText('Pattern help', { exact: true }).click();
  await expect(page.locator('app-split-editor code')).toContainText('([A-Z0-9-]+)');
  await page.getByTestId('workflow-advanced').click();
  await expect(page.getByTestId('split-explanation')).toContainText('captured identifier changes');
});

test('guided steps preserve advanced settings and unsaved edits through navigation and saving', async ({ page, request }) => {
  const id = `wizard-${randomUUID().replaceAll('-', '')}`;
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'New workflow', exact: true }).click();
  await page.getByTestId('catalog-name').fill('Wizard preservation ' + id);
  page.once('dialog', dialog => dialog.dismiss());
  await page.getByRole('button', { name: 'Back to workflows', exact: true }).click();
  await expect(page.getByTestId('catalog-name')).toHaveValue('Wizard preservation ' + id);
  await page.getByTestId('workflow-next').click();
  await page.getByTestId('workflow-advanced').click();
  await page.getByLabel('Stable ID', { exact: true }).fill(id);
  await page.getByLabel('Workflow instructions', { exact: true }).fill('Preserve this advanced classification instruction.');
  await page.getByRole('checkbox', { name: 'Reviewer task settings', exact: true }).check();
  await page.getByRole('spinbutton', { name: 'Task priority', exact: true }).fill('7');
  await page.getByTestId('workflow-guided').click();
  await expect(page.getByTestId('workflow-step')).toContainText('Step 2 of 4');
  await expect(page.getByTestId('capability-editor')).not.toBeVisible();
  await page.getByTestId('workflow-next').click();
  await page.getByRole('checkbox', { name: 'Require order', exact: true }).check();
  await page.getByTestId('workflow-next').click();
  await expect(page.getByRole('region', { name: 'Workflow summary' })).toContainText('Review tasks');
  await page.getByTestId('catalog-save').click();
  await expect(page.getByTestId('catalog-publish')).toBeEnabled();
  await expect(page.getByTestId('workflow-step')).toContainText('Step 4 of 4');
  const catalog = await (await request.get('/api/workflows')).json();
  const saved = catalog.entries.find((entry: any) => entry.id === id);
  expect(saved.workflow.instructions).toBe('Preserve this advanced classification instruction.');
  expect(saved.workflow.capabilities.operations.defaultPriority).toBe(7);
  expect(saved.workflow.requiredIdentifierKinds).toContain('order');
  await page.getByRole('button', { name: 'Back to workflows', exact: true }).click();
  await page.getByRole('button', { name: `Edit Wizard preservation ${id} version 1`, exact: true }).click();
  await page.getByTestId('workflow-advanced').click();
  await expect(page.getByLabel('Workflow instructions', { exact: true })).toHaveValue(saved.workflow.instructions);
  await expect(page.getByRole('spinbutton', { name: 'Task priority', exact: true })).toHaveValue('7');
  await page.getByTestId('workflow-guided').click();
  for (let i = 0; i < 3; i++) await page.getByTestId('workflow-next').click();
  await page.getByTestId('catalog-publish').click();
  await expect(page.getByTestId('catalog-new-version')).toBeEnabled();
  await page.getByTestId('catalog-new-version').click();
  await expect(page.getByTestId('workflow-step')).toContainText('Step 1 of 4');
  await expect(page.getByTestId('catalog-name')).toBeEnabled();
});

test('theme follows the system until chosen, persists across reload and keeps guided pages readable on mobile', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.emulateMedia({ colorScheme: 'light' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await menuAction(page, 'theme-toggle');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.evaluate(() => localStorage.getItem('paprika-theme'))).toBe('dark');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'New workflow', exact: true }).click();
  await page.getByTestId('catalog-name').fill('Synthetic theme preview');
  const colors = await page.getByTestId('catalog-name').evaluate(input => {
    const style = getComputedStyle(input);
    return { background: style.backgroundColor, foreground: style.color, scheme: getComputedStyle(document.documentElement).colorScheme };
  });
  expect(colors).toEqual({ background: 'rgb(24, 34, 45)', foreground: 'rgb(225, 234, 242)', scheme: 'dark' });
  for (const width of [360, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await expect(page.getByTestId('workflow-step')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await page.getByTestId('workflow-next').click();
  await menuAction(page, 'theme-toggle');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await expect(page.getByTestId('workflow-step')).toContainText('Step 2 of 4');
  await page.getByTestId('workflow-back').click();
  await expect(page.getByTestId('catalog-name')).toHaveValue('Synthetic theme preview');
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Back to workflows', exact: true }).click();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});
