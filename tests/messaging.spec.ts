import { test, expect } from '@playwright/test';
import { menuAction } from './navigation';

const headers = { 'X-PaperworkClassifier': '1' };

test('business rejection stays terminal instead of becoming a retryable dead letter', async ({ request }) => {
  const created = await (await request.post('/api/packets/demo', { headers })).json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${created.id}`)).json()).status).toBe('review');
  let packet = await (await request.get(`/api/packets/${created.id}`)).json();
  packet = await (await request.put(`/api/packets/${packet.id}/review`, {
    headers, data: { revision: packet.revision, documents: packet.documents, approve: true },
  })).json();
  const response = await request.post(`/api/packets/${packet.id}/deliveries`, {
    headers, data: { revision: packet.revision, scenario: 'reject' },
  });
  expect(response.ok()).toBeTruthy();
  const delivery = await response.json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${packet.id}/deliveries`)).json())[0].status).toBe('rejected');
  expect((await request.post(`/api/deliveries/${delivery.id}/retry`, { headers })).status()).toBe(409);
  await expect.poll(async () => (await (await request.get('/api/messaging')).json()).queues.find((q: { name: string }) => q.name === 'delivery').pending).toBe(0);
  const messaging = await (await request.get('/api/messaging')).json();
  expect(messaging.deadLetters.some((d: { deliveryId: string }) => d.deliveryId === delivery.id)).toBe(false);
  expect((await (await request.get(`/api/packets/${packet.id}/deliveries`)).json())[0].attempts).toBe(1);
});

test('durable queue summary and dead-letter replay use real persisted failures', async ({ page, request }) => {
  test.setTimeout(120_000);
  const startedAt = Date.now();
  const created = await (await request.post('/api/packets/demo', { headers })).json();
  await expect.poll(async () => (await (await request.get(`/api/packets/${created.id}`)).json()).status).toBe('review');
  let packet = await (await request.get(`/api/packets/${created.id}`)).json();
  const approval = await request.put(`/api/packets/${packet.id}/review`, {
    headers, data: { revision: packet.revision, documents: packet.documents, approve: true },
  });
  expect(approval.ok()).toBeTruthy();
  packet = await approval.json();
  const queued = await request.post(`/api/packets/${packet.id}/deliveries`, {
    headers, data: { revision: packet.revision, scenario: 'transient-always' },
  });
  expect(queued.ok()).toBeTruthy();
  const delivery = await queued.json();

  const messaging = async () => (await request.get('/api/messaging')).json();
  await expect.poll(async () => (await messaging()).deadLetters.some((d: { deliveryId: string }) => d.deliveryId === delivery.id), { timeout: 60_000 }).toBe(true);
  const failed = (await (await request.get(`/api/packets/${packet.id}/deliveries`)).json())[0];
  expect(failed.status).toBe('failed');
  expect(failed.attempts).toBeGreaterThan(1);
  const state = await messaging();
  expect(state.mode).toBe('durable-local');
  expect(state.queues.map((q: { name: string }) => q.name).sort()).toEqual(['continuation', 'delivery', 'processing']);
  const dead = state.deadLetters.find((d: { deliveryId: string }) => d.deliveryId === delivery.id);
  expect(dead.messageType).toBe('ProcessDelivery');
  expect(dead.reason).toBeTruthy();
  expect(Number.isNaN(Date.parse(dead.failedAt))).toBe(false);
  expect(Date.parse(dead.failedAt)).toBeGreaterThanOrEqual(startedAt - 1000);
  expect(Date.parse(dead.failedAt)).toBeLessThanOrEqual(Date.now() + 1000);
  expect(Object.keys(dead).sort()).toEqual(['deliveryId', 'failedAt', 'id', 'messageType', 'packetId', 'reason']);
  expect((await request.post(`/api/messaging/deadletters/${dead.id}/replay`)).status()).toBe(403);

  await page.goto('/');
  await menuAction(page, 'classification-advanced');
  await page.getByTestId('operations-toggle').click();
  await expect(page.getByTestId('messaging-queue')).toHaveCount(3);
  const row = page.getByTestId('deadletter-item').filter({ hasText: delivery.id });
  await expect(row).toBeVisible();
  const replayEvent = page.waitForResponse(r => r.url().endsWith(`/deadletters/${dead.id}/replay`) && r.request().method() === 'POST');
  await row.getByTestId('replay-deadletter').click();
  const replay = await replayEvent;
  expect(replay.ok()).toBeTruthy();
  expect((await replay.json()).status).toBe('queued');
  const duplicateReplay = await request.post(`/api/messaging/deadletters/${dead.id}/replay`, { headers });
  expect([404, 409]).toContain(duplicateReplay.status());
  await expect.poll(async () => {
    const attempts = (await (await request.get(`/api/packets/${packet.id}/deliveries`)).json())[0].attempts;
    const letters = (await messaging()).deadLetters.filter((d: { deliveryId: string }) => d.deliveryId === delivery.id);
    return attempts > failed.attempts && letters.length === 1;
  }, { timeout: 60_000 }).toBe(true);
  await page.getByTestId('messaging-refresh').click();
  await expect(row).toBeVisible();
  await expect(row.getByTestId('replay-deadletter')).toBeEnabled();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  expect((await (await request.get(`/api/packets/${packet.id}`)).json()).status).toBe('approved');
});
