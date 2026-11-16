import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 60_000,
  expect: { timeout: 20_000 },
  workers: 1,
  use: {
    baseURL: process.env['PAPRIKA_TEST_URL'] ?? 'http://127.0.0.1:4200',
    headless: true,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  reporter: 'list',
});
