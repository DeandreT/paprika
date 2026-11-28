import { expect, test, type APIRequestContext, type APIResponse, type Locator, type Page } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { Packet, Page as SourcePage } from '../client/src/models';
import type { ExtractionHistory } from '../client/src/field-review';
import { jsonFromZip } from './export-zip';
import { textPdf } from './fixtures';

const headers = { 'X-paprika': '1' };
const proofDir = process.env.PAPRIKA_FIELD_REVIEW_PROOF_DIR;
const notes = {
  dock: 'Use the assigned door on the dispatch copy.',
  cartons: 'The first character is a one, not an I.',
  memo: 'Internal routing note; leave it out of the receiving record.',
};
const alternateDock = 'EAST-14';
const filePages = [
  'INVOICE | SYNTHETIC SAMPLE\nPage 1 of 2\nDock: WEST-12\nCartons: I8\nDispatch memo: NIGHT SORT',
  `INVOICE | DISPATCH COPY\nPage 2 of 2\nAssigned door: ${alternateDock}`,
];

test.use({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1, colorScheme: 'light', reducedMotion: 'reduce' });

async function json<T>(response: APIResponse): Promise<T> {
  expect(response.ok(), `${response.status()} ${await response.text()}`).toBe(true);
  return response.json();
}

async function makePacket(request: APIRequestContext): Promise<Packet> {
  const suffix = randomUUID().slice(0, 8);
  const definitionId = `dock-fields-${suffix}`;
  const workflowId = `field-review-${suffix}`;
  const definition = await json<{ sha256: string }>(await request.post('/api/extraction/definitions', { headers, data: {
    id: definitionId, version: 1, documentType: 'invoice', tables: [],
    fields: [
      { name: 'dock', kind: 'text', label: 'Dock', required: true },
      { name: 'cartons', kind: 'number', label: 'Cartons', required: true },
      { name: 'memo', kind: 'text', label: 'Dispatch memo' },
      { name: 'seal', kind: 'text', label: 'Seal' },
    ],
  } }));
  const draft = await json<{ version: number; revision: number }>(await request.post('/api/workflows/catalog', { headers, data: {
    kind: 'workflow', id: workflowId, name: 'Synthetic dock review', description: 'Two-page synthetic invoice.',
    workflow: {
      release: { id: 'logistics', version: 1 }, mode: 'simulation', providerProfile: 'default', model: 'local-simulation-1',
      instructions: '', defaultSplit: { mode: 'consecutive', pageCount: 1, pattern: '' },
      identifierKinds: [], requiredIdentifierKinds: [],
      capabilities: { extraction: { definitionId, version: 1, sha256: definition.sha256 } },
    },
  } }));
  await json(await request.post(`/api/workflows/catalog/workflow/${workflowId}/${draft.version}/publish`, {
    headers, data: { revision: draft.revision },
  }));
  const queued = await json<Packet>(await request.post('/api/packets', { headers, multipart: {
    workflowId, workflowVersion: String(draft.version),
    files: { name: `Synthetic dock invoice ${suffix}.pdf`, mimeType: 'application/pdf', buffer: textPdf(filePages) },
  } }));
  let packet = queued;
  await expect.poll(async () => {
    packet = await json<Packet>(await request.get(`/api/packets/${queued.id}`));
    return packet.status;
  }).toBe('review');
  return packet;
}

async function openGuidedReview(page: Page, packet: Packet) {
  await page.goto('/');
  await page.getByTestId('existing-packets').click();
  await page.getByRole('searchbox', { name: 'Search packets' }).fill(packet.name);
  await page.getByRole('navigation', { name: 'Existing packets' }).getByRole('button')
    .filter({ has: page.getByText(packet.name, { exact: true }) }).click();
  if (packet.status === 'approved') await page.getByRole('button', { name: 'Back to review', exact: true }).click();
  await expect(page.getByTestId('classification-step')).toContainText('Review');
  await expect(page.getByRole('heading', { level: 1, name: packet.name, exact: true })).toBeVisible();
  await expect(page.getByTestId('guided-document-type')).toHaveValue('invoice');
}

async function captureWorkspace(page: Page, filename: string) {
  if (!proofDir) return;
  const preview = page.getByRole('region', { name: 'Source preview' }).locator('canvas');
  await expect(preview).toBeVisible();
  // A newly allocated PDF canvas is visible before the first non-white glyph is drawn.
  await expect.poll(() => preview.evaluate((canvas: HTMLCanvasElement) => {
    const pixels = canvas.getContext('2d')!.getImageData(0, 0, canvas.width, canvas.height).data;
    for (let i = 0; i < pixels.length; i += 4)
      if (pixels[i + 3] && pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100) return true;
    return false;
  })).toBe(true);
  await page.evaluate(async () => { await document.fonts.ready; window.scrollTo(0, 0); });
  await mkdir(proofDir, { recursive: true });
  await page.screenshot({ path: join(proofDir, filename), animations: 'disabled' });
}

// Point PAPRIKA_TEST_URL at the built origin/main UI and run only this test to capture
// its workspace. Reopen the printed packet ID on the final build using the same data store.
test('Guided field review baseline screenshot', async ({ page, request }) => {
  test.skip(!proofDir || !!process.env.PAPRIKA_FIELD_REVIEW_PACKET_ID, 'Only used to capture the pre-change workspace.');
  const packet = await makePacket(request);
  await openGuidedReview(page, packet);
  await expect(page.getByTestId('review-field')).toHaveCount(0);
  await captureWorkspace(page, 'before.png');
  console.log(`PAPRIKA_FIELD_REVIEW_PACKET_ID=${packet.id}`);
});

function field(page: Page, name: string) {
  return page.getByRole('main', { name: 'Guided classification' }).locator(`[data-testid="review-field"][data-field="${name}"]`);
}

async function saveCorrection(page: Page, row: Locator, packetId: string, note: string) {
  await row.getByTestId('field-note').fill(note);
  const saved = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith(`/api/extraction/${packetId}/corrections`));
  await row.getByTestId('field-save').click();
  const response = await saved;
  expect(response.ok(), await response.text()).toBe(true);
  await expect(row.getByTestId('field-edit')).toBeVisible();
}

function reference(page: SourcePage, text: string) {
  const offset = page.text.indexOf(text);
  expect(offset, `Exact span "${text}" on source page ${page.pageNumber}`).toBeGreaterThanOrEqual(0);
  return { pageId: page.id, sourceId: page.sourceId, pageNumber: page.pageNumber, offset, length: text.length };
}

test('Guided review corrects fields, retains feedback and downloads the approved values', async ({ page, request }) => {
  test.setTimeout(90_000);
  const existingId = process.env.PAPRIKA_FIELD_REVIEW_PACKET_ID;
  const packet = existingId ? await json<Packet>(await request.get(`/api/packets/${existingId}`)) : await makePacket(request);
  expect(packet).toMatchObject({ testRun: false, mode: 'simulation', status: 'review', workflow: { id: expect.stringMatching(/^field-review-/) } });
  expect(packet.documents).toHaveLength(1);
  expect(packet.pages).toHaveLength(2);
  const [first, second] = packet.pages;
  const [doc] = packet.documents;
  expect(doc).toMatchObject({ type: 'invoice', pageIds: [first.id, second.id] });
  const historyUrl = `/api/extraction/${packet.id}`;
  const initial = await json<ExtractionHistory>(await request.get(historyUrl));
  const original = initial.runs.find(run => run.id === initial.currentConfiguredRunId);
  expect(original?.results[0].fields).toMatchObject([
    { name: 'dock', value: 'WEST-12', status: 'extracted' },
    { name: 'cartons', value: null, status: 'invalid', evidence: [{ text: 'I8' }] },
    { name: 'memo', value: 'NIGHT SORT', status: 'extracted' },
    { name: 'seal', value: null, status: 'missing', evidence: [] },
  ]);
  await openGuidedReview(page, packet);
  const dock = field(page, 'dock'), cartons = field(page, 'cartons'), memo = field(page, 'memo');
  await expect(dock.getByTestId('field-source')).toContainText('WEST-12');
  await captureWorkspace(page, 'after.png');

  await dock.getByTestId('field-edit').click();
  await dock.getByTestId('field-category').selectOption({ label: 'Wrong text selected' });
  await dock.getByTestId('field-page').selectOption(second.id);
  const pageText = dock.getByTestId('field-page-text');
  const start = (await pageText.inputValue()).indexOf(alternateDock);
  expect(start).toBeGreaterThanOrEqual(0);
  await pageText.focus();
  await pageText.evaluate((element: HTMLTextAreaElement, offset) => element.setSelectionRange(offset, offset), start);
  for (let i = 0; i < alternateDock.length; i++) await pageText.press('Shift+ArrowRight');
  await expect(dock.getByTestId('field-value')).toHaveValue(alternateDock);
  if (proofDir) {
    await dock.getByTestId('field-note').fill(notes.dock);
    await dock.screenshot({ path: join(proofDir, 'wrong-selection.png'), animations: 'disabled' });
  }
  await saveCorrection(page, dock, packet.id, notes.dock);
  await expect(dock.getByTestId('field-final')).toContainText(alternateDock);

  await cartons.getByTestId('field-edit').click();
  await cartons.getByTestId('field-category').selectOption({ label: 'Text read incorrectly' });
  await cartons.getByTestId('field-value').fill('018');
  await saveCorrection(page, cartons, packet.id, notes.cartons);
  await expect(cartons.getByTestId('field-final')).toContainText('18');

  await memo.getByTestId('field-edit').click();
  await memo.getByTestId('field-category').selectOption({ label: 'Omit from delivery' });
  await saveCorrection(page, memo, packet.id, notes.memo);
  expect((await request.get(`/api/packets/${packet.id}/export`)).status()).toBe(409);

  await page.getByTestId('finish-classification-review').click();
  await page.getByTestId('guided-approve').click();
  await expect(page.getByTestId('classification-step')).toContainText('Export');
  const downloading = page.waitForEvent('download');
  await page.getByTestId('guided-export').click();
  const download = await downloading;
  expect(download.suggestedFilename()).toMatch(/\.zip$/);
  expect(await download.failure()).toBeNull();
  const zip = await readFile((await download.path())!);
  const manifest = jsonFromZip(zip, 'manifest.json');
  expect(manifest.revision).toBe(packet.revision + 4); // Three corrections, then approval.
  const policy = packet.workflow!.capabilities!.extraction!;
  expect(jsonFromZip(zip, 'fields.json')).toEqual({
    schemaVersion: 1, packetId: packet.id, revision: manifest.revision, workflowHash: packet.workflow!.hash,
    definition: { id: policy.definitionId, version: policy.version, sha256: policy.sha256 },
    documents: [{ documentId: doc.id, type: 'invoice', fields: [
      { name: 'dock', kind: 'text', status: 'corrected', value: alternateDock, evidence: [reference(second, alternateDock)] },
      { name: 'cartons', kind: 'number', status: 'corrected', value: '18', evidence: [reference(first, 'I8')] },
      { name: 'memo', kind: 'text', status: 'corrected', value: null, evidence: [reference(first, 'NIGHT SORT')] },
      { name: 'seal', kind: 'text', status: 'missing', value: null, evidence: [] },
    ] }],
  });

  // Start a fresh UI session on the approved revision, including the carry across approval.
  const approved = await json<Packet>(await request.get(`/api/packets/${packet.id}`));
  expect(approved).toMatchObject({ status: 'approved', revision: manifest.revision });
  await openGuidedReview(page, approved);
  await expect(dock.getByTestId('field-source')).toHaveText('WEST-12');
  await expect(dock.getByTestId('field-final')).toHaveText(alternateDock);
  await expect(memo.getByTestId('field-source')).toHaveText('NIGHT SORT');
  await expect(memo.getByTestId('field-final')).toHaveText('Omitted from delivery');
  await expect(field(page, 'seal').getByTestId('field-source')).toHaveText('missing');
  await expect(field(page, 'seal').getByTestId('field-history')).toHaveCount(0);
  for (const [row, category, note] of [
    [dock, 'Wrong text selected', notes.dock],
    [cartons, 'Text read incorrectly', notes.cartons],
    [memo, 'Omit from delivery', notes.memo],
  ] as const) {
    const history = row.getByTestId('field-history');
    await history.locator('summary').click();
    await expect(history).toContainText(category);
    await expect(history).toContainText(note);
  }
  const retained = await json<ExtractionHistory>(await request.get(historyUrl));
  expect(retained.runs.find(run => run.id === original!.id)).toEqual(original);
  expect(retained.corrections.filter(entry => entry.correction)).toMatchObject([
    { correction: { field: 'dock', category: 'wrong-selection', reason: notes.dock }, normalizedValue: alternateDock,
      before: { value: 'WEST-12', evidence: [{ pageId: first.id, text: 'WEST-12' }] },
      after: { value: alternateDock, evidence: [{ pageId: second.id, text: alternateDock }] } },
    { correction: { field: 'cartons', category: 'transcription', reason: notes.cartons }, normalizedValue: '18',
      before: { status: 'invalid' }, after: { value: '18', evidence: [{ text: 'I8' }] } },
    { correction: { field: 'memo', category: 'omit', reason: notes.memo }, normalizedValue: null,
      before: { status: 'extracted', value: 'NIGHT SORT' }, after: { status: 'corrected', value: null, evidence: [{ text: 'NIGHT SORT' }] } },
  ]);
});
