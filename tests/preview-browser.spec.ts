import { expect, test as base, type Locator, type Page } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { textPdf } from './fixtures';
import { menuAction } from './navigation';

type RGB = readonly [number, number, number];
const red: RGB = [1, 0, 0], green: RGB = [0, 1, 0], blue: RGB = [0, 0, 1], yellow: RGB = [1, 1, 0];
const sourceRoute = '**/api/packets/*/sources/*';

// Optional packaged-engine acceptance: pixels alone could also be a successful
// PDF.js fallback. Observe the complete test, including zoom and packet switches.
const test = base.extend<{ verifyEngine: void }>({
  verifyEngine: [async ({ context, page }, use) => {
    if (process.env['PAPRIKA_EXPECT_RENDERER'] !== 'leafview') return use();
    const loaded = new Set<string>(), fallback: string[] = [];
    context.on('response', r => { if (r.ok()) loaded.add(r.url()); });
    context.on('request', r => {
      if (new URL(r.url()).pathname === '/assets/pdf.worker.min.mjs') fallback.push(r.url());
    });
    await use();
    expect(fallback, 'Leafview must render these fixtures without PDF.js fallback').toEqual([]);
    const entry = await page.locator('meta[name="paprika-preview-engine"]').getAttribute('content');
    expect(entry).toMatch(/^\/assets\/preview-engine\/[0-9a-f]{64}\/wasm-engine\.js$/);
    const module = new URL(entry!, page.url());
    for (const path of ['wasm-engine.js', 'wasm-worker.js', 'pkg/folio_wasm.wasm'])
      expect(loaded, `successful browser response for ${path}`).toContain(new URL(path, module).href);
  }, { auto: true }],
});

function pdf(name: string, colors: RGB[]) {
  return {
    name, mimeType: 'application/pdf',
    buffer: textPdf(colors.map((_, i) => `INVOICE\nOrder: SYNTHETIC-PREVIEW\nPage ${i + 1} of ${colors.length}`), colors),
  };
}

// Read rendered pixels, not canvas dimensions (even an untouched canvas has a size).
// Copy the sample to a new canvas so this works for 2D and bitmaprenderer alike.
function pixel(container: Locator) {
  return container.evaluate(element => {
    const canvas = element.querySelector('canvas');
    if (!canvas?.width || !canvas.height) return null;
    const sample = document.createElement('canvas');
    sample.width = sample.height = 1;
    const ctx = sample.getContext('2d')!;
    ctx.drawImage(canvas, Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1, 0, 0, 1, 1);
    return Array.from(ctx.getImageData(0, 0, 1, 1).data);
  });
}

const rgba = (rgb: RGB) => [...rgb.map(c => c * 255), 255];
const painted = (container: Locator, rgb: RGB) =>
  expect.poll(() => pixel(container), { message: `center of ${container} should be ${rgb}` }).toEqual(rgba(rgb));

async function upload(page: Page, files: ReturnType<typeof pdf>[]) {
  const accepted = page.waitForResponse(r => r.url().endsWith('/api/packets') && r.request().method() === 'POST');
  await page.getByTestId('upload-input').setInputFiles(files);
  const response = await accepted;
  expect(response.ok()).toBe(true);
  return await response.json() as { id: string; name: string };
}

test('guided preview paints the last page selected while a real PDF is still loading', async ({ page, context }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Upload packet', exact: true })).toBeEnabled();
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  let sourceLoads = 0;
  await context.route(sourceRoute, async route => {
    sourceLoads++;
    await gate;
    await route.continue();
  });
  try {
    await upload(page, [pdf(`guided-preview-${randomUUID()}.pdf`, [red, green, blue, yellow])]);
    await expect.poll(() => sourceLoads).toBe(1);
    const next = page.getByRole('button', { name: 'Next page', exact: true });
    const previous = page.getByRole('button', { name: 'Previous page', exact: true });
    for (const button of [next, next, next, previous]) await button.click();
    await expect(page.getByText('Page 3 of 4', { exact: true })).toBeVisible();
    release();
    const preview = page.locator('.large-preview');
    await painted(preview, blue);
    // At 2× the guided UI asks for high-resolution pixels. Check the actual
    // raster, not the CSS size: the optional renderer has a distinct bitmap path.
    for (let i = 0; i < 4; ++i) await page.getByTestId('preview-zoom-in').click();
    await expect.poll(() => preview.locator('canvas').evaluate(c => (c as HTMLCanvasElement).width)).toBeGreaterThanOrEqual(1600);
    await painted(preview, blue);
    await previous.click();
    await painted(preview, green);
    expect(sourceLoads, 'page changes share the same small source PDF').toBe(1);
    await expect(preview.getByText('Preview unavailable', { exact: false })).toHaveCount(0);
  } finally {
    release();
  }
});

test('advanced review paints visible thumbnails on demand and replaces pixels on packet changes', async ({ page, context }) => {
  await page.setViewportSize({ width: 1360, height: 850 });
  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await expect(page.getByRole('button', { name: 'Upload packet', exact: true }).first()).toBeEnabled();
  const loads: string[] = [];
  await context.route(sourceRoute, route => {
    loads.push(new URL(route.request().url()).pathname);
    return route.continue();
  });
  const run = randomUUID(), name = `primary-${run}.pdf`, tailName = `tail-${run}.pdf`;
  const colors = Array.from({ length: 48 }, (_, i) => [red, green, blue][i % 3]);
  const firstPacket = await upload(page, [pdf(name, colors), pdf(tailName, [yellow])]);
  const preview = page.locator('.large-preview');
  const thumbs = page.getByTestId('page-item');
  await expect(thumbs).toHaveCount(colors.length + 1);
  const first = thumbs.first(), tail = thumbs.last();
  await first.scrollIntoViewIfNeeded();
  await expect(tail).not.toBeInViewport();
  await painted(preview, red);
  await painted(first, red);
  // The tail belongs to a *different source*. Fetching it before it is near the
  // viewport would defeat lazy loading even if the canvas stayed blank.
  const sourceLink = page.getByRole('link', { name: 'Open original source' });
  const primaryUrl = await sourceLink.getAttribute('href');
  expect(loads).toEqual([primaryUrl]);
  expect(await pixel(tail)).not.toEqual(rgba(yellow));
  await tail.scrollIntoViewIfNeeded();
  await painted(tail, yellow);
  await tail.click();
  await painted(preview, yellow);
  expect(loads).toEqual([primaryUrl, await sourceLink.getAttribute('href')]);

  // Same file name and source page number in another packet, different bytes.
  await first.click();
  await painted(preview, red);
  const secondPacket = await upload(page, [pdf(name, [blue])]);
  await expect(page.getByTestId('packet-name')).toHaveText(secondPacket.name);
  await painted(preview, blue);
  await page.getByRole('navigation', { name: 'Packets', exact: true }).getByRole('button').filter({ hasText: firstPacket.name }).click();
  await expect(page.getByTestId('packet-name')).toHaveText(firstPacket.name);
  await expect(sourceLink).toHaveAttribute('href', primaryUrl!);
  await painted(preview, red);
});

test('guided review reopens an evicted source after visiting a packet of independent PDFs', async ({ page, context }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Upload packet', exact: true })).toBeEnabled();
  const previewWorkers = () => page.workers().filter(w => /\/(?:pdf\.worker\.min\.mjs|wasm-worker\.js)$/.test(new URL(w.url()).pathname));
  const loads: string[] = [];
  // Routing disables Chromium's HTTP cache. A request on revisit then tells us
  // whether the document was actually reopened, without reaching into the app.
  await context.route(sourceRoute, route => {
    loads.push(new URL(route.request().url()).pathname);
    return route.continue();
  });
  const colors = [green, blue, yellow, red, blue, red, green, yellow];
  const run = randomUUID();
  await upload(page, colors.map((color, i) => pdf(`${run}-${i}.pdf`, [color])));
  const preview = page.locator('.large-preview');
  const link = page.getByRole('link', { name: 'Open original source' });
  await painted(preview, colors[0]);
  const firstUrl = await link.getAttribute('href');
  for (const color of colors.slice(1)) {
    await page.getByTestId('next-document').click();
    await painted(preview, color);
  }
  expect(new Set(loads).size).toBe(colors.length);
  expect(loads.filter(url => url === firstUrl)).toHaveLength(1);
  // Releasing a cache key alone is insufficient: the discarded document's
  // worker (and its associated PDF/wasm memory) must stop too.
  expect(previewWorkers().length).toBeGreaterThan(0);
  await expect.poll(() => previewWorkers().length).toBeLessThan(colors.length);
  for (let i = 1; i < colors.length; ++i) await page.getByTestId('previous-document').click();
  await expect(link).toHaveAttribute('href', firstUrl!);
  await painted(preview, colors[0]);
  expect(loads.filter(url => url === firstUrl), 'old document was released and can be opened again').toHaveLength(2);
});
