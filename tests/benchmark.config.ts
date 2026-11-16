import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './benchmark',
  testMatch: '*.benchmark.ts',
  timeout: 180_000,
  expect: { timeout: 20_000 },
  workers: 1,
  use: { baseURL: process.env['PAPRIKA_TEST_URL'] ?? 'http://127.0.0.1:5080' },
  reporter: 'list',
});
