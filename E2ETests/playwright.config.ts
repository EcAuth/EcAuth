import { defineConfig, devices } from '@playwright/test';

/**
 * Read environment variables from file.
 * https://github.com/motdotla/dotenv
 */
// import dotenv from 'dotenv';
// import path from 'path';
// dotenv.config({ path: path.resolve(__dirname, '.env') });

/**
 * See https://playwright.dev/docs/test-configuration.
 */
export default defineConfig({
  testDir: './tests',
  /* Run tests in files in parallel */
  fullyParallel: true,
  /* Fail the build on CI if you accidentally left test.only in the source code. */
  forbidOnly: !!process.env.CI,
  /* Retry on CI only */
  retries: process.env.CI ? 2 : 0,
  /* Opt out of parallel tests on CI. */
  workers: process.env.CI ? 1 : undefined,
  /* Reporter to use. See https://playwright.dev/docs/test-reporters */
  reporter: 'html',
  /* Shared settings for all the projects below. See https://playwright.dev/docs/api/class-testoptions. */
  use: {
    /* Base URL to use in actions like `await page.goto('/')`. */
    // baseURL: 'http://127.0.0.1:3000',

    /* Collect trace when retrying the failed test. See https://playwright.dev/docs/trace-viewer */
    trace: 'on-first-retry',
    screenshot: {
      mode: 'only-on-failure',
      fullPage: true,
    },
    video: 'retain-on-failure',
    ignoreHTTPSErrors: true,
    // Docker環境でのホスト名解決のため
    extraHTTPHeaders: {},
    // mockopenidproviderのホスト名を127.0.0.1にマッピング
    baseURL: undefined,
  },

  /* Configure projects for major browsers */
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // ホスト名解決のマッピング。
        // - accounts.ec-auth.io: Account 申込フローの passkey 登録/認証で使う accounts テナント
        //   （3 セグメント以上で TenantMiddleware が tenant=accounts に解決）。CI / ローカル両方で必要。
        //   WebAuthn の rp_id を origin と一致させ、B2BUser 解決を accounts テナントで行うため、
        //   passkey ページはこのホストで配信する。
        // - mockopenidprovider: ローカル Docker 環境の MockIdP 解決（GitHub Actions では不要）。
        launchOptions: {
          args: [
            '--host-resolver-rules=' + [
              'MAP accounts.ec-auth.io 127.0.0.1',
              ...(process.env.CI
                ? []
                : ['MAP mockopenidprovider:8081 127.0.0.1:9091', 'MAP mockopenidprovider:8080 127.0.0.1:9090']),
            ].join(','),
          ],
        },
      },
    },

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
  outputDir: 'test-results/',
  /* Run your local dev server before starting the tests */
  // webServer: {
  //   command: 'npm run start',
  //   url: 'http://127.0.0.1:3000',
  //   reuseExistingServer: !process.env.CI,
  // },
});
