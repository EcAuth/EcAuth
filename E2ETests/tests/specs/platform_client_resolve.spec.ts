import { test, expect, APIRequestContext, request } from '@playwright/test';

test.describe('Platform API: client-resolve エンドポイントのE2Eテスト', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const clientResolveEndpoint = `${baseUrl}/platform/v1/client-resolve`;
  const clientId = process.env.DEFAULT_CLIENT_ID || 'client_id';

  let apiContext: APIRequestContext;

  test.beforeAll(async () => {
    apiContext = await request.newContext({
      ignoreHTTPSErrors: true,
    });
  });

  test.afterAll(async () => {
    await apiContext.dispose();
  });

  test('正常系: 有効な client_id でテナント情報が返る', async () => {
    const response = await apiContext.get(clientResolveEndpoint, {
      params: { client_id: clientId },
    });

    expect(response.status()).toBe(200);

    const body = await response.json();
    console.log('client-resolve response:', JSON.stringify(body, null, 2));

    expect(body).toHaveProperty('tenant_name');
    expect(body).toHaveProperty('base_url');
    expect(body).toHaveProperty('organization_name');

    expect(typeof body.tenant_name).toBe('string');
    expect(body.tenant_name.length).toBeGreaterThan(0);
    expect(body.base_url).toContain(body.tenant_name);
    expect(body.base_url).toMatch(/^https:\/\//);
  });

  test('異常系: 存在しない client_id で 404 が返る', async () => {
    const response = await apiContext.get(clientResolveEndpoint, {
      params: { client_id: 'nonexistent-client-id-12345' },
    });

    expect(response.status()).toBe(404);

    const body = await response.json();
    expect(body).toHaveProperty('error', 'not_found');
    expect(body).toHaveProperty('error_description');
  });

  test('バリデーション: client_id パラメータなしで 400 が返る', async () => {
    const response = await apiContext.get(clientResolveEndpoint);

    expect(response.status()).toBe(400);

    const body = await response.json();
    expect(body).toHaveProperty('error', 'invalid_request');
    expect(body).toHaveProperty('error_description');
  });

  test('バリデーション: 空の client_id で 400 が返る', async () => {
    const response = await apiContext.get(clientResolveEndpoint, {
      params: { client_id: '' },
    });

    expect(response.status()).toBe(400);

    const body = await response.json();
    expect(body).toHaveProperty('error', 'invalid_request');
  });

  test('CORS: Origin ヘッダ付きリクエストで CORS レスポンスヘッダが返る', async () => {
    // CI 環境（Development）では AllowedOrigins に baseUrl が含まれる
    // 本番環境では https://ec-auth.io が含まれる
    const origin = new URL(baseUrl).origin;

    const corsContext = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: {
        'Origin': origin,
      },
    });

    const response = await corsContext.get(clientResolveEndpoint, {
      params: { client_id: clientId },
    });

    expect(response.status()).toBe(200);

    const headers = response.headers();
    console.log('CORS response headers:', JSON.stringify(headers, null, 2));

    expect(headers['access-control-allow-origin']).toBe(origin);

    await corsContext.dispose();
  });

  test('テナントスコープAPI への影響がないことを確認', async () => {
    // /platform/ 以外の既存エンドポイントが正常に動作することを確認
    const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || `${baseUrl}/v1/token`;
    const response = await apiContext.post(tokenEndpoint, {
      form: {
        grant_type: 'authorization_code',
        code: 'invalid-code',
        redirect_uri: 'https://localhost:8081/callback',
        client_id: clientId,
      },
    });

    // invalid-code なので 400 が返るが、エンドポイント自体は動作している
    expect(response.status()).toBe(400);
    const body = await response.json();
    expect(body).toHaveProperty('error');
  });
});
