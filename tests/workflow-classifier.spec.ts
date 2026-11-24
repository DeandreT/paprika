import { expect, test, type Page } from '@playwright/test';
import type { CatalogEntry, Config, WorkflowCatalog } from '../client/src/models';
import { menuAction } from './navigation';

// Synthetic catalog responses let us vary server credentials without calling a live provider.
async function catalogFixture(page: Page) {
  const release = { id: 'documents', version: 1 };
  const workflow = (id: string, providerProfile: string, model: string): CatalogEntry => ({
    id, kind: 'workflow', name: id, version: 1, revision: 1, status: 'published', description: '',
    workflow: { release, mode: 'live', providerProfile, model, instructions: '',
      defaultSplit: { mode: 'classifier', pageCount: 1, pattern: '' }, identifierKinds: [], requiredIdentifierKinds: [] },
  });
  const local = workflow('local', 'default', 'local-simulation-1');
  local.workflow!.mode = 'simulation';
  const catalog: WorkflowCatalog = {
    canUpload: true,
    defaultWorkflow: { id: local.id, version: 1 }, stats: [], policies: [], providerProfiles: [],
    liveModel: 'jev-test',
    classifierProviders: [{ id: 'jev', model: 'jev-test', configured: false }, { id: 'openai', model: 'decision-test', configured: true }],
    entries: [local, workflow('jev', 'jev', 'jev-test'), workflow('openai', 'openai', 'decision-test'),
      { ...workflow('legacy', 'default', 'decision-test'), resolvedClassifierProvider: 'openai' },
      workflow('unresolved', 'default', 'decision-test'),
      { ...release, name: 'Documents', kind: 'release', revision: 1, status: 'published', description: '', release: { types: [] } }],
  };
  const config: Config = { mode: 'live', ocrConfigured: true, classifierConfigured: true, maxUploadMb: 25, documentTypes: [] };
  await page.route('**/api/config', route => route.fulfill({ json: config }));
  await page.route('**/api/packets', route => route.fulfill({ json: [] }));
  await page.route('**/api/workflows', route => route.fulfill({ json: catalog }));
  await page.route('**/api/workflows/catalog', async route => {
    const saved = { ...route.request().postDataJSON(), id: 'saved', revision: 1 };
    catalog.entries.push(saved);
    await route.fulfill({ json: saved });
  });
  return { catalog, config, workflow };
}

test('live workflow pins the chosen model across credential changes and simulation uses local rules', async ({ page }) => {
  const { catalog } = await catalogFixture(page);
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'New workflow', exact: true }).click();
  await page.getByTestId('catalog-name').fill('Pinned decisions');
  await page.getByTestId('workflow-next').click();
  const mode = page.getByRole('combobox', { name: 'Workflow mode' });
  const classifier = page.getByTestId('workflow-classifier');
  const status = page.getByTestId('workflow-classifier-status');
  await mode.selectOption('live');
  await expect(page.getByTestId('workflow-next')).toBeDisabled();
  await expect(classifier.locator('option[value="custom"]')).toHaveCount(0);
  await classifier.selectOption('jev');
  await expect(status).toContainText('Jev · jev-test · Not configured');
  await classifier.selectOption('openai');
  await expect(status).toContainText('OpenAI Decisions · decision-test · Configured');
  await page.getByTestId('workflow-advanced').click();
  await expect(classifier).toHaveValue('openai');
  await page.getByTestId('catalog-save').click();
  await expect(page.getByText('Draft saved.', { exact: true })).toBeVisible();
  expect(catalog.entries.at(-1)?.workflow).toMatchObject({ providerProfile: 'openai', model: 'decision-test' });

  catalog.classifierProviders[0].configured = true;
  await page.getByRole('button', { name: 'Reload catalog' }).click();
  await expect(classifier.locator('option[value="jev"]')).toHaveText('Jev · Configured');
  await expect(classifier).toHaveValue('openai');
  await expect(status).toContainText('decision-test');
  await mode.selectOption('simulation');
  await expect(classifier).toHaveCount(0);
  await expect(page.getByText('Simulation · Local rules, no OCR · local-simulation-1')).toBeVisible();
});

test('a self-hosted classifier is a selectable peer and saves an explicit model pin', async ({ page }) => {
  const { catalog } = await catalogFixture(page);
  catalog.classifierProviders.push({ id: 'custom', model: 'local-decisions-v1', configured: true });
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'New workflow', exact: true }).click();
  await page.getByTestId('catalog-name').fill('Pinned self-hosted classifier');
  await page.getByTestId('workflow-next').click();
  await page.getByRole('combobox', { name: 'Workflow mode' }).selectOption('live');
  const classifier = page.getByTestId('workflow-classifier');
  const status = page.getByTestId('workflow-classifier-status');
  await expect(classifier.locator('option[value="openai"]')).toHaveText('OpenAI Decisions · Configured');
  await expect(classifier.locator('option[value="custom"]')).toHaveText('Self-hosted classifier · Configured');
  await expect(classifier).toHaveValue('');
  await classifier.selectOption('custom');
  await expect(status).toHaveText('Self-hosted classifier · local-decisions-v1 · Configured');
  await page.getByTestId('catalog-save').click();
  await expect(page.getByText('Draft saved.', { exact: true })).toBeVisible();
  expect(catalog.entries.at(-1)?.workflow).toMatchObject({ providerProfile: 'custom', model: 'local-decisions-v1' });
  await expect(classifier).toHaveValue('custom');
});

test('upload readiness checks the selected classifier, pinned model, OCR and scoped access', async ({ page }) => {
  const { catalog, config } = await catalogFixture(page);
  await page.goto('/');
  const select = page.getByTestId('workflow-select');
  const upload = page.getByRole('button', { name: 'Upload packet', exact: true }).first();
  const status = page.getByTestId('workflow-upload-status');
  await select.selectOption('jev@1');
  await expect(status).toContainText('Jev · jev-test · Not configured');
  await expect(upload).toBeDisabled();
  await select.selectOption('legacy@1');
  await expect(status).toContainText('OpenAI Decisions · decision-test · Configured');
  await expect(upload).toBeEnabled();
  await select.selectOption('unresolved@1');
  await expect(status).toContainText('Unresolved classifier');
  await expect(upload).toBeDisabled();

  config.ocrConfigured = false;
  await page.reload();
  await expect(upload).toBeEnabled(); // local simulation
  await select.selectOption('openai@1');
  await expect(status).toContainText('OCR not configured');
  await expect(upload).toBeDisabled();
  catalog.classifierProviders[1].model = 'decision-next';
  config.ocrConfigured = true;
  await page.reload();
  await select.selectOption('openai@1');
  await expect(status).toContainText('decision-test · Pinned model unavailable');
  await expect(upload).toBeDisabled();

  // A shared reader sees only the provider metadata exposed for readable workflows.
  catalog.entries = catalog.entries.filter(e => ['legacy', 'unresolved'].includes(e.id));
  catalog.classifierProviders = [{ id: 'openai', model: 'decision-test', configured: true }];
  catalog.defaultWorkflow = { id: 'legacy', version: 1 };
  catalog.canUpload = false;
  catalog.liveModel = '';
  await page.reload();
  await menuAction(page, 'classification-advanced');
  await expect(status).toContainText('OpenAI Decisions · decision-test · Configured');
  await expect(status).toContainText('Administrator access required to upload.');
  await expect(upload).toBeDisabled();
  await select.selectOption('unresolved@1');
  await expect(status).toContainText('Unresolved classifier');
  await expect(upload).toBeDisabled();
});

test('self-hosted intake requires the explicit, current binding and provider readiness', async ({ page }) => {
  const { catalog, workflow } = await catalogFixture(page);
  const model = 'local-decisions-v1';
  const provider = { id: 'custom' as const, model, configured: true };
  catalog.classifierProviders.push(provider);
  catalog.entries.push(
    workflow('self-hosted', 'custom', model),
    { ...workflow('stale-endpoint', 'custom', model), classifierBindingUnavailable: true },
    { ...workflow('stale-model', 'custom', 'local-decisions-v0'), classifierBindingUnavailable: true },
    workflow('legacy-self-hosted-model', 'default', model),
  );
  await page.goto('/');
  const select = page.getByTestId('workflow-select');
  const upload = page.getByRole('button', { name: 'Upload packet', exact: true });
  const status = page.getByTestId('workflow-upload-status');
  await select.selectOption('self-hosted@1');
  await expect(status).toContainText('Self-hosted classifier · local-decisions-v1 · Configured');
  await expect(upload).toBeEnabled();
  await select.selectOption('stale-endpoint@1');
  await expect(status).toContainText('Self-hosted classifier · local-decisions-v1 · Pinned endpoint unavailable');
  await expect(upload).toBeDisabled();
  await select.selectOption('stale-model@1');
  await expect(status).toContainText('Self-hosted classifier · local-decisions-v0 · Pinned model unavailable');
  await expect(upload).toBeDisabled();
  await select.selectOption('legacy-self-hosted-model@1');
  await expect(status).toContainText('Unresolved classifier · local-decisions-v1');
  await expect(upload).toBeDisabled();

  provider.configured = false;
  await page.reload();
  await select.selectOption('self-hosted@1');
  await expect(status).toContainText('Self-hosted classifier · local-decisions-v1 · Not configured');
  await expect(upload).toBeDisabled();

  catalog.classifierProviders = catalog.classifierProviders.filter(p => p.id !== 'custom');
  await page.reload();
  await select.selectOption('self-hosted@1');
  await expect(status).toContainText('Self-hosted classifier · local-decisions-v1 · Pinned model unavailable');
  await expect(upload).toBeDisabled();
});

test('a stale self-hosted draft can be saved against the current configuration before publication', async ({ page }) => {
  const { catalog, workflow } = await catalogFixture(page);
  const model = 'local-decisions-v1';
  catalog.classifierProviders.push({ id: 'custom', model, configured: true });
  const draft = {
    ...workflow('stale-draft', 'custom', model),
    status: 'draft' as const, name: 'Stale self-hosted draft', classifierBindingUnavailable: true,
  };
  catalog.entries.push(draft);
  await page.route('**/api/workflows/catalog/workflow/stale-draft/1', async route => {
    const body = route.request().postDataJSON();
    expect(route.request().method()).toBe('PUT');
    expect(body).not.toHaveProperty('classifierBindingUnavailable');
    expect(body.workflow).toMatchObject({ providerProfile: 'custom', model });
    const saved = { ...body, revision: 2 };
    catalog.entries[catalog.entries.indexOf(draft)] = saved;
    await route.fulfill({ json: saved });
  });
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'Edit Stale self-hosted draft version 1' }).click();
  await page.getByTestId('workflow-advanced').click();
  const status = page.getByTestId('workflow-classifier-status');
  const publish = page.getByTestId('catalog-publish');
  await expect(status).toHaveText('Self-hosted classifier · local-decisions-v1 · Pinned endpoint unavailable');
  await expect(publish).toBeDisabled();
  await page.getByRole('button', { name: 'Use current classifier configuration' }).click();
  const save = page.getByTestId('catalog-save');
  await expect(save).toBeEnabled();
  await expect(publish).toBeDisabled();
  await save.click();
  await expect(status).toHaveText('Self-hosted classifier · local-decisions-v1 · Configured');
  await expect(publish).toBeEnabled();
});

test('copied stale binding needs an explicit classifier and saved model before publication', async ({ page }) => {
  const { catalog } = await catalogFixture(page);
  const copy = structuredClone(catalog.entries.find(e => e.id === 'openai')!);
  Object.assign(copy, { id: 'repair', name: 'Repair draft', status: 'draft', classifierBindingNeedsRepair: true });
  copy.workflow!.model = 'previous-decision-model';
  catalog.entries.push(copy);
  await page.route('**/api/workflows/catalog/workflow/repair/1', async route => {
    const saved = route.request().postDataJSON();
    expect(saved).not.toHaveProperty('classifierBindingNeedsRepair');
    expect(saved.workflow).toMatchObject({ providerProfile: 'openai', model: 'decision-test' });
    catalog.entries[catalog.entries.indexOf(copy)] = saved;
    await route.fulfill({ json: saved });
  });
  await page.goto('/');
  await menuAction(page, 'workflow-manager-toggle');
  await page.getByRole('button', { name: 'Edit Repair draft version 1' }).click();
  await page.getByTestId('workflow-advanced').click();
  const classifier = page.getByTestId('workflow-classifier');
  await expect(classifier).toHaveValue('');
  await expect(page.getByTestId('workflow-classifier-status')).toContainText('previous-decision-model');
  await expect(page.getByTestId('catalog-publish')).toBeDisabled();
  await classifier.selectOption('openai');
  await expect(page.getByTestId('workflow-classifier-status')).toContainText('decision-test · Configured');
  await expect(page.getByTestId('catalog-publish')).toBeDisabled();
  await page.getByTestId('catalog-save').click();
  await expect(page.getByTestId('catalog-publish')).toBeEnabled();
});
