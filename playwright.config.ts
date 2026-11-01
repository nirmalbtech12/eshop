import { defineConfig, devices } from '@playwright/test';
require('dotenv').config({ path: './.env', quiet: true });
import path from 'path';

if (process.env.LEMU_HOST_TEST_RESULTS) {
  process.env.USERNAME1 ||= 'bob';
  process.env.PASSWORD ||= 'Pass123$';
}

export const STORAGE_STATE = path.join(__dirname, 'playwright/.auth/user.json');

/**
 * See https://playwright.dev/docs/test-configuration.
 */
export default defineConfig({
  testDir: './e2e',
  /* Seeded users share server-side basket state, so UI journeys run serially. */
  fullyParallel: false,
  /* Fail the build on CI if you accidentally left test.only in the source code. */
  forbidOnly: !!process.env.CI,
  /* Retry on CI only */
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  /* Reporter to use. See https://playwright.dev/docs/test-reporters */
  reporter: process.env.LEMU_HOST_TEST_RESULTS
    ? [['html', { open: 'never' }], ['json', { outputFile: path.join(process.env.LEMU_HOST_TEST_RESULTS, 'playwright.json') }]]
    : [['html', { open: 'never' }]],
  /* Shared settings for all the projects below. See https://playwright.dev/docs/api/class-testoptions. */
  use: {
    /* Base URL to use in actions like `await page.goto('/')`. */
    baseURL: 'http://localhost:5045',

    /* Collect trace when retrying the failed test. See https://playwright.dev/docs/trace-viewer */
    trace: 'on-first-retry',
    ...devices['Desktop Chrome'],
  },

  /* Configure projects for major browsers */
  projects: [
    {
      name: 'setup',
      testMatch: '**/*.setup.ts',
    },
    {
      name: 'e2e tests logged in',
      testMatch: ['**/CartTest.spec.ts', '**/CheckoutTest.spec.ts'],
      dependencies: ['setup'],
      use: {
        storageState: STORAGE_STATE,
      },
    },
    {
      name: 'e2e tests without logged in',
      testMatch: ['**/BrowseItemTest.spec.ts'],
    }
    // {
    //   name: 'chromium',
    //   use: { ...devices['Desktop Chrome'] },
    // },

    // {
    //   name: 'firefox',
    //   use: { ...devices['Desktop Firefox'] },
    // },

    // {
    //   name: 'webkit',
    //   use: { ...devices['Desktop Safari'] },
    // },

    /* Test against mobile viewports. */
    // {
    //   name: 'Mobile Chrome',
    //   use: { ...devices['Pixel 5'] },
    // },
    // {
    //   name: 'Mobile Safari',
    //   use: { ...devices['iPhone 12'] },
    // },

    /* Test against branded browsers. */
    // {
    //   name: 'Microsoft Edge',
    //   use: { ...devices['Desktop Edge'], channel: 'msedge' },
    // },
    // {
    //   name: 'Google Chrome',
    //   use: { ...devices['Desktop Chrome'], channel: 'chrome' },
    // },
  ],

  /* Run your local dev server before starting the tests */
  webServer: {
    command: process.env.LEMU_HOST_TEST_RESULTS
      ? 'dotnet run --no-build --no-restore --no-launch-profile --project src/eShop.AppHost/eShop.AppHost.csproj'
      : 'dotnet run --project src/eShop.AppHost/eShop.AppHost.csproj',
    url: 'http://localhost:5045',
    reuseExistingServer: !process.env.CI && !process.env.LEMU_HOST_TEST_RESULTS,
    stderr: 'pipe',
    stdout: 'pipe',
    timeout: process.env.CI || process.env.LEMU_HOST_TEST_RESULTS ? (5 * 60_000) : 60_000,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
    env: {
      ESHOP_USE_HTTP_ENDPOINTS: '1',
      ASPNETCORE_ENVIRONMENT: 'Development',
      DOTNET_DASHBOARD_OTLP_ENDPOINT_URL: 'http://localhost:18889',
      DOTNET_DASHBOARD_HTTP_ENDPOINT_URL: 'http://localhost:18888',
      DOTNET_RESOURCE_SERVICE_ENDPOINT_URL: 'http://localhost:18890',
      ASPIRE_ALLOW_UNSECURED_TRANSPORT: 'true',
      USERNAME1: process.env.USERNAME1 || 'bob',
      PASSWORD: process.env.PASSWORD || 'Pass123$',
    },
  },
});
