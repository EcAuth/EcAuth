import { test, expect, APIRequestContext, request } from '@playwright/test';

/**
 * 申込は本番サイト URL を必須とする（EcAuth#482）。
 *
 * テストサイトだけの申込を許すと、紐づく本番の無いサンドボックス Org
 * （parent_organization_id が null）ができる。このサンドボックスは
 * 「1 本番あたりテストは 1 件」の判定（AccountController が ParentOrganizationId で
 * 数える）に引っかからないため、後から本番を追加するとサンドボックスが 2 件並ぶ
 * 状態を作れてしまう。孤立を後から親に紐づけ直す手段も無い。
 *
 * 申込フォーム側（ecauth-website の signup.html / signup.js）も本番必須に揃えてあるが、
 * フォームを経由しない直接 POST を防ぐのは API 側のこの検証。
 */
test.describe('申込のサイト URL 必須条件', () => {
  const tenantBaseDomain = process.env.E2E_TENANT_BASE_DOMAIN;
  const remote = Boolean(tenantBaseDomain);

  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const accountsHost =
    process.env.E2E_ACCOUNTS_HOST || (remote ? `stg-accounts.${tenantBaseDomain}` : 'accounts.ec-auth.io');
  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;

  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;

  let api: APIRequestContext;

  test.beforeAll(async () => {
    api = await request.newContext({
      ignoreHTTPSErrors: true,
      ...(remote ? {} : { extraHTTPHeaders: { Host: accountsHost } }),
    });
  });

  test.afterAll(async () => {
    await api?.dispose();
  });

  const signupBody = (overrides: Record<string, unknown>) => ({
    email: `e2e-required-${runSuffix}@e2e.ec-auth.io`,
    organization_name: `E2E Required ${runSuffix}`,
    contact_name: 'E2E Tester',
    ec_cube_version: '4',
    ...overrides,
  });

  test('テストサイト URL だけの申込は 422 で拒否される', async () => {
    const response = await api.post(`${accountsApiBaseUrl}/api/signup/request`, {
      data: signupBody({
        production_site_url: null,
        test_site_url: `https://e2e-testonly-${runSuffix}.test/`,
      }),
    });

    expect(response.status()).toBe(422);
    const body = await response.json();
    expect(body.error).toBe('invalid_site_url');
    // どの入力欄の問題かをフォームが赤くできるよう field を返す。
    expect(body.field).toBe('production_site_url');
  });

  test('サイト URL を両方とも省いた申込も 422 で拒否される', async () => {
    const response = await api.post(`${accountsApiBaseUrl}/api/signup/request`, {
      data: signupBody({ production_site_url: null, test_site_url: null }),
    });

    expect(response.status()).toBe(422);
    expect((await response.json()).error).toBe('invalid_site_url');
  });
});
