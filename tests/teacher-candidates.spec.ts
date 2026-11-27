import { expect, test, type Page } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { menuAction } from './navigation';

async function openQuality(page: Page) {
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Quality and training', exact: true }).click();
  const panel = page.locator('app-quality-panel');
  await panel.getByText('Quality, independent QA and trained text models', { exact: true }).click();
  return panel;
}

test('the selected dataset exports reviewed text and a demo packet supplies no provider predictions', async ({ page, request }) => {
  const suffix = randomUUID();
  const headers = { 'X-paprika': '1' };
  const sets = [];
  for (const source of ['one', 'two']) {
    const response = await request.post('/api/training/datasets', { headers, data: { name: `Synthetic labels ${source} ${suffix}` } });
    expect(response.ok(), await response.text()).toBe(true);
    sets.push(await response.json());
  }
  const example = { id: 'manual-sample', label: 'invoice', text: 'ARTIFICIAL invoice: oranges.', synthetic: true, provenance: 'Synthetic manual label' };
  const seeded = await request.post(`/api/training/datasets/${sets[0].id}/examples`, { headers, data: { examples: [example] } });
  expect(seeded.ok(), await seeded.text()).toBe(true);
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  const created = page.waitForResponse(r => r.url().endsWith('/api/packets/demo') && r.request().method() === 'POST');
  await page.getByTestId('demo-button').click();
  const demo = await (await created).json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${demo.id}`)).json()).status).toBe('review');
  const panel = await openQuality(page);
  const picker = panel.getByRole('combobox', { name: 'Training dataset', exact: true });
  const review = panel.locator('app-candidate-review');
  await expect(picker).toHaveValue('');
  await expect(review).toHaveCount(0);
  await picker.selectOption(sets[0].id);
  await review.getByText('Create candidate labels from predictions', { exact: true }).click();
  await expect(review).toContainText(`Selected packet: ${demo.id}`);
  await review.getByRole('button', { name: 'Import selected packet’s predictions' }).click();
  await expect(review.getByText('New predictions imported: 0.', { exact: true })).toBeVisible();
  await expect(review).toContainText('No candidate labels in this dataset yet.');

  for (const dataset of sets) {
    await picker.selectOption(dataset.id);
    const downloaded = page.waitForEvent('download');
    await review.getByRole('button', { name: 'Download JSON with training text' }).click();
    const file = await downloaded;
    expect(file.suggestedFilename()).toBe(`paprika-${dataset.id}-reviewed.json`);
    const payload = JSON.parse(await readFile((await file.path())!, 'utf8'));
    expect(payload).toMatchObject({ format: 'paprika.reviewed-page-labels.v1', dataset, fieldFeedback: [] });
    if (dataset.id === sets[0].id) {
      expect(payload.examples).toHaveLength(1);
      expect(payload.examples[0]).toMatchObject({ example, origin: 'labeled-example', teacherReview: null });
    } else expect(payload.examples).toEqual([]);
  }
});

// Synthetic immutable receipts exercise UI states that need more than one provider.
// Real provider receipt capture, comparison and transaction semantics have server tests.
test('reviewers inspect independent comparisons, choose labels, and reconcile a changed batch', async ({ page }) => {
  await page.route('**/api/config', route => route.fulfill({ json: { mode: 'simulation', maxUploadMb: 25, documentTypes: [] } }));
  await page.route('**/api/packets', route => route.fulfill({ json: [] }));
  await page.route('**/api/workflows', route => route.fulfill({ json: { entries: [], stats: [], policies: [], providerProfiles: [], classifierProviders: [] } }));
  await page.route('**/api/operations-platform/session', route => route.fulfill({ json: { mode: 'local', authenticated: false, identity: null } }));
  await page.route('**/api/training/models', route => route.fulfill({ json: [] }));
  await page.route('**/api/training/datasets', route => route.fulfill({ json: [
    { id: 'desk', name: 'Synthetic receiving desk' }, { id: 'other', name: 'Synthetic outbound desk' },
  ] }));
  function candidate(id: string, pageNumber: number, comparison: string, labels: string[]) {
    return {
      id, version: `${id}:1`, comparison, status: 'pending', label: null as string | null,
      reviewer: null as string | null, reviewedAt: null as string | null, text: `ARTIFICIAL page ${pageNumber} training text.`,
      labels: ['invoice', 'packing_list', 'bol'],
      predictions: labels.map((label, index) => ({
        packetId: `packet-${index + 1}`, pageId: `${id}-page-${index + 1}`, revision: comparison === 'single' ? null : 3,
        sourceName: 'Synthetic receiving packet.pdf', pageNumber, proposedLabel: label,
        provider: comparison === 'single' ? null : `mock-provider-${index + 1}`,
        model: comparison === 'single' ? null : `synthetic-v${index + 1}`, requestedModel: comparison === 'single' ? 'pinned-v2' : null,
        provenance: comparison === 'single' ? 'Legacy receipt' : 'Synthetic provider receipt', synthetic: true,
      })),
    };
  }
  const rows = [candidate('mixed', 1, 'disagreement', ['invoice', 'packing_list']),
    candidate('match', 2, 'agreement', ['packing_list', 'packing_list']), candidate('unpaired', 3, 'single', ['unknown'])];
  const base = '**/api/training/datasets/desk';
  await page.route(`${base}/candidates`, route => route.fulfill({ json: rows }));
  await page.route('**/api/training/datasets/other/candidates', route => route.fulfill({ json: [] }));
  let submissions = 0;
  await page.route(`${base}/candidates/review`, async route => {
    expect(route.request().headers()['x-paprika']).toBe('1');
    const { decisions } = route.request().postDataJSON();
    if (++submissions === 1) {
      rows[2].version = 'unpaired:2';
      rows[2].status = 'rejected';
      rows[2].reviewer = 'another reviewer';
      await route.fulfill({ status: 409, json: { error: 'The evidence or review changed.' } });
      return;
    }
    const expected = submissions === 2 ? [
      { id: 'mixed', version: 'mixed:1', decision: 'accepted', label: 'invoice' },
      { id: 'match', version: 'match:1', decision: 'corrected', label: 'bol' },
      { id: 'unpaired', version: 'unpaired:2', decision: 'rejected' },
    ] : [{ id: 'mixed', version: 'mixed:2', decision: 'corrected', label: 'bol' }];
    expect(decisions).toEqual(expected);
    for (const change of decisions) {
      const row = rows.find(item => item.id === change.id)!;
      Object.assign(row, { status: change.decision, label: change.label ?? null, reviewer: 'test reviewer',
        reviewedAt: '2026-01-01T12:00:00Z', version: `${row.id}:${submissions}` });
    }
    await route.fulfill({ json: { reviewed: decisions.length } });
  });

  await page.goto('/');
  const panel = await openQuality(page);
  const picker = panel.getByRole('combobox', { name: 'Training dataset', exact: true });
  await picker.selectOption('desk');
  const review = panel.locator('app-candidate-review');
  await review.getByText('Create candidate labels from predictions', { exact: true }).click();
  const articles = review.getByRole('article');
  const mixed = review.getByRole('article', { name: 'Synthetic receiving packet.pdf, page 1' });
  const match = review.getByRole('article', { name: 'Synthetic receiving packet.pdf, page 2' });
  const unpaired = review.getByRole('article', { name: 'Synthetic receiving packet.pdf, page 3' });
  const save = review.getByRole('button', { name: 'Save staged decisions' });
  await expect(articles.first()).toContainText('Disagreement');
  await expect(unpaired).toContainText('Single prediction');
  await mixed.getByText('Inspect predictions (2) and page text').click();
  await expect(mixed.getByText('ARTIFICIAL page 1 training text.')).toBeVisible();
  await expect(mixed).toContainText('mock-provider-2 / synthetic-v2');
  await unpaired.getByText('Inspect predictions (1) and page text').click();
  await expect(unpaired).toContainText('Legacy receipt');
  await expect(unpaired).toContainText('revision not recorded');
  await expect(unpaired).toContainText('Provider not recorded / Model not recorded');
  await expect(unpaired).toContainText('requested: pinned-v2');
  await mixed.getByLabel('Review decision').selectOption('accepted');
  await expect(mixed.getByLabel('Accepted proposal')).toHaveValue('');
  await expect(save).toBeDisabled();
  await mixed.getByLabel('Accepted proposal').selectOption('invoice');
  await review.getByRole('button', { name: 'Sample an agreement' }).click();
  await expect(articles).toHaveCount(1);
  await expect(match.getByText('ARTIFICIAL page 2 training text.')).toBeVisible();
  await match.getByLabel('Review decision').selectOption('corrected');
  await expect(match.getByLabel('Corrected label').locator('option[value="packing_list"]')).toHaveCount(0);
  await match.getByLabel('Corrected label').selectOption('bol');
  await review.getByLabel('Comparison', { exact: true }).selectOption('all');
  await expect(unpaired.getByLabel('Review decision').locator('option[value="accepted"]')).toBeDisabled();
  await unpaired.getByLabel('Review decision').selectOption('rejected');
  await expect(review.getByText('3 staged', { exact: true })).toBeVisible();
  await save.click();
  await expect(review.getByRole('alert')).toContainText('A candidate changed.');
  await expect(save).toBeDisabled();
  await review.getByRole('button', { name: 'Reload candidates' }).click();
  await expect(review.getByText('2 staged', { exact: true })).toBeVisible();
  await review.getByLabel('Review status').selectOption('all');
  await expect(unpaired).toContainText('another reviewer');
  await unpaired.getByLabel('Review decision').selectOption('rejected');
  await save.click();
  await expect(review.getByText('Review decisions saved: 3.', { exact: true })).toBeVisible();
  await expect(mixed).toContainText('Last review: accepted');
  await expect(match).toContainText('Last review: corrected');
  await mixed.getByLabel('Review decision').selectOption('corrected');
  await mixed.getByLabel('Corrected label').selectOption('bol');
  await save.click();
  await expect(mixed).toContainText('Last review: corrected');
  await expect(articles).toHaveCount(3);
  await match.getByLabel('Review decision').selectOption('accepted');
  await picker.selectOption('other');
  await expect(review).toContainText('No candidate labels in this dataset yet.');
  await expect(review).not.toContainText('Synthetic receiving packet.pdf');
  await picker.selectOption('desk');
  await review.getByLabel('Review status').selectOption('all');
  await expect(review.getByText('0 staged', { exact: true })).toBeVisible();
  expect(submissions).toBe(3);
});
