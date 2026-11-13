import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';
import { createHash, randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';

test('packaged native PDF imaging supports UI derivatives, visual templates and reviewed pixel-redacted downloads', async ({ page, request }) => {
  test.setTimeout(180_000);
  const suffix = randomUUID().replaceAll('-', '');
  const headers = { 'X-PaperworkClassifier': '1' };
  const workflowId = `imaging-ui-${suffix}`;
  const templateId = `imaging-template-${suffix}`;
  const filename = `ARTIFICIAL-imaging-${suffix}.pdf`;
  const secret = `PRIVATE-IMAGING-${suffix}`;
  const original = textPdf([`INVOICE\n${secret}\nARTIFICIAL packaged native imaging acceptance`]);
  const sha256 = (bytes: Buffer) => createHash('sha256').update(bytes).digest('hex');
  const originalHash = sha256(original);

  // Use an isolated simulation workflow; never change the shared active/default workflow.
  const catalogResponse = await request.get('/api/workflows');
  expect(catalogResponse.ok(), await catalogResponse.text()).toBe(true);
  const catalog = await catalogResponse.json();
  const baseline = catalog.entries.find((entry: any) => entry.kind === 'workflow' && entry.id === 'general-logistics' && entry.version === 1);
  expect(baseline).toBeTruthy();
  const draftResponse = await request.post('/api/workflows/catalog', { headers, data: {
    kind: 'workflow', id: workflowId, name: `ARTIFICIAL native imaging ${suffix}`, description: 'Synthetic packaged PDFium/SkiaSharp browser acceptance',
    workflow: { ...baseline.workflow, mode: 'simulation', model: 'local-simulation-1', defaultSplit: { mode: 'separate', pageCount: 1, pattern: '' }, capabilities: null },
  } });
  expect(draftResponse.ok(), await draftResponse.text()).toBe(true);
  const draft = await draftResponse.json();
  const publication = await request.post(`/api/workflows/catalog/workflow/${draft.id}/${draft.version}/publish`, { headers, data: { revision: draft.revision } });
  expect(publication.ok(), await publication.text()).toBe(true);
  const uploaded = await request.post('/api/packets', { headers, multipart: {
    files: { name: filename, mimeType: 'application/pdf', buffer: original }, workflowId, workflowVersion: String(draft.version),
  } });
  expect(uploaded.ok(), await uploaded.text()).toBe(true);
  const queued = await uploaded.json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${queued.id}`)).json()).status, { timeout: 30_000 }).toBe('review');
  const packet = await (await request.get(`/api/packets/${queued.id}`)).json();
  expect(packet.workflow.mode).toBe('simulation');
  expect(packet.pages).toHaveLength(1);
  const sourceId = packet.pages[0].sourceId;
  const pageId = packet.pages[0].id;
  const sourceUrl = `/api/packets/${packet.id}/sources/${sourceId}`;
  const sourceBefore = await request.get(sourceUrl);
  expect(sourceBefore.ok(), await sourceBefore.text()).toBe(true);
  expect(sha256(await sourceBefore.body())).toBe(originalHash);

  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await page.getByRole('navigation', { name: 'Packets', exact: true }).getByRole('button').filter({ hasText: filename }).click();
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Extraction and images', exact: true }).click();
  const panel = page.locator('app-extraction-panel');
  await panel.getByText('Extraction, visual templates, image processing and outputs', { exact: true }).click();
  await expect(panel.getByRole('combobox', { name: 'Source', exact: true }).locator(`option[value="${sourceId}"]`)).toHaveCount(1);
  await panel.getByRole('combobox', { name: 'Source', exact: true }).selectOption(sourceId);
  await panel.getByLabel('Source page number', { exact: true }).fill('1');
  await panel.getByRole('combobox', { name: 'Rotation', exact: true }).selectOption({ label: '0°' });
  await panel.getByLabel('Grayscale', { exact: true }).check();
  await panel.getByLabel('Contrast', { exact: true }).fill('1.5');
  const derivativeSaved = page.waitForResponse(response => response.url().endsWith(`/api/preprocessing/${packet.id}`) && response.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Create immutable image derivative', exact: true }).click();
  const derivativeResponse = await derivativeSaved;
  expect(derivativeResponse.ok(), await derivativeResponse.text()).toBe(true);
  const derivative = await derivativeResponse.json();
  expect(derivative).toMatchObject({ packetId: packet.id, sourceId, pageNumber: 1, originalSha256: originalHash, policy: { rotation: 0, grayscale: true, contrast: 1.5 } });
  expect(derivative.width).toBeGreaterThan(0);
  expect(derivative.height).toBeGreaterThan(0);
  expect(derivative.width * derivative.height).toBeLessThanOrEqual(4_000_000);
  await panel.getByText('Derivatives and hashes', { exact: true }).click();
  await expect(panel.getByText('Derivatives and hashes', { exact: true }).locator('..')).toContainText(derivative.sha256);
  const imageResponse = await request.get(`/api/preprocessing/${packet.id}/derivatives/${derivative.id}/image`);
  expect(imageResponse.ok(), await imageResponse.text()).toBe(true);
  expect(imageResponse.headers()['content-type']).toContain('image/png');
  const imageBytes = await imageResponse.body();
  expect(imageBytes.subarray(0, 8)).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  expect(imageBytes.readUInt32BE(16)).toBe(derivative.width);
  expect(imageBytes.readUInt32BE(20)).toBe(derivative.height);
  expect(sha256(imageBytes)).toBe(derivative.sha256);

  // Train from the explicitly selected original PDF page, then compare decoded/rendered features.
  await panel.getByLabel('Template ID', { exact: true }).fill(templateId);
  await panel.getByLabel('Version', { exact: true }).fill('1');
  await panel.getByLabel('Workflow label', { exact: true }).fill('invoice');
  const templateSaved = page.waitForResponse(response => response.url().endsWith('/api/recognition/templates') && response.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Train new immutable image template', exact: true }).click();
  const templateResponse = await templateSaved;
  expect(templateResponse.ok(), await templateResponse.text()).toBe(true);
  const trained = await templateResponse.json();
  expect(trained.template).toMatchObject({ id: templateId, version: 1, label: 'invoice', sourceSha256: originalHash, pageNumber: 1, featureAlgorithm: 'gray-grid-32-v1' });
  expect(trained.template.features).toHaveLength(1024);
  await panel.getByLabel('Minimum similarity', { exact: true }).fill('0.99');
  await panel.getByLabel('Minimum competing-label margin', { exact: true }).fill('0');
  const compared = page.waitForResponse(response => response.url().endsWith(`/api/recognition/${packet.id}/run`) && response.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Compare all templates (read-only predictions)', exact: true }).click();
  const comparisonResponse = await compared;
  expect(comparisonResponse.ok(), await comparisonResponse.text()).toBe(true);
  const recognition = await comparisonResponse.json();
  const match = recognition.find((result: any) => result.pageId === pageId && result.templates.some((reference: any) => reference.id === templateId && reference.sha256 === trained.sha256));
  expect(match).toBeTruthy();
  expect(match).toMatchObject({ packetId: packet.id, label: 'invoice', sourceSha256: originalHash, packetRevision: packet.revision });
  expect(match.score).toBeGreaterThanOrEqual(0.99);
  expect(match.renderer).toContain('PDFium');

  // Approval is API setup, while the reviewed region selection/generation/download are real UI actions.
  const current = await (await request.get(`/api/packets/${packet.id}`)).json();
  const approval = await request.put(`/api/packets/${packet.id}/review`, { headers, data: { revision: current.revision, documents: current.documents, approve: true } });
  expect(approval.ok(), await approval.text()).toBe(true);
  const approved = await approval.json();
  expect(approved.status).toBe('approved');
  await page.reload();
  await menuAction(page, 'classification-advanced');
  await page.getByRole('navigation', { name: 'Packets', exact: true }).getByRole('button').filter({ hasText: filename }).click();
  await menuAction(page, 'platform-toggle');
  await page.getByRole('button', { name: 'Extraction and images', exact: true }).click();
  await panel.getByText('Extraction, visual templates, image processing and outputs', { exact: true }).click();
  await panel.getByRole('combobox', { name: 'Format', exact: true }).selectOption('redacted-image-pdf');
  // Fixture text occupies the upper quarter; no rotation means derivative and output raster coordinates agree.
  const regions = [{ pageId, x: 0, y: 0, width: derivative.width, height: Math.ceil(derivative.height / 4) }];
  await panel.getByLabel('Image redaction regions JSON: pageId, x, y, width, height (original pixels)', { exact: true }).fill(JSON.stringify(regions));
  const generated = page.waitForResponse(response => response.url().endsWith(`/api/outputs/${packet.id}`) && response.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Generate immutable output', exact: true }).click();
  const generatedResponse = await generated;
  expect(generatedResponse.ok(), await generatedResponse.text()).toBe(true);
  const artifact = await generatedResponse.json();
  expect(artifact).toMatchObject({ packetId: packet.id, revision: approved.revision, format: 'redacted-image-pdf', preview: false });
  const downloadStarted = page.waitForEvent('download');
  await panel.getByRole('link', { name: 'Download redacted-image-pdf', exact: true }).click();
  const download = await downloadStarted;
  expect(download.suggestedFilename()).toBe('redacted-image-pdf.pdf');
  expect(await download.failure()).toBeNull();
  const stream = await download.createReadStream();
  expect(stream).not.toBeNull();
  const chunks: Buffer[] = [];
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  const pdfBytes = Buffer.concat(chunks);
  expect(pdfBytes.subarray(0, 5).toString('ascii')).toBe('%PDF-');
  expect(pdfBytes.length).toBeGreaterThan(1_000);
  expect(sha256(pdfBytes)).toBe(artifact.sha256);
  expect(pdfBytes.toString('latin1')).not.toContain(secret);
  expect(pdfBytes.toString('latin1')).toContain('/Subtype /Image');
  expect(artifact.sourceHashes).toEqual([originalHash]);
  const sourceAfter = await request.get(sourceUrl);
  expect(sourceAfter.ok(), await sourceAfter.text()).toBe(true);
  expect(sha256(await sourceAfter.body())).toBe(originalHash);
});
