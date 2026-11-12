import { test, expect } from '@playwright/test';

test('same-origin build serves the PDF worker while unknown service routes remain 404', async ({ request }) => {
  const worker = await request.get('/assets/pdf.worker.min.mjs');
  expect(worker.ok()).toBeTruthy();
  expect(worker.headers()['content-type']).toMatch(/javascript/);
  expect(await worker.text()).not.toContain('<app-root');
  for (const path of ['/api/missing-endpoint', '/openapi/missing-document.json']) {
    const response = await request.get(`http://127.0.0.1:5080${path}`);
    expect(response.status()).toBe(404);
    expect(await response.text()).not.toContain('<app-root');
  }
});
