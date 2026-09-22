import { test, expect, APIRequestContext, BrowserContext, Page, request } from '@playwright/test';
import { randomUUID } from 'crypto';
import { signupAndGetAccountToken, fetchSignupClient } from '../helpers/accounts';
import { registerB2BPasskey, authenticateB2BPasskey } from '../helpers/b2b-passkey';
import { createMailbox, Mailbox } from '../helpers/mailbox';
import { generatePkcePair } from '../helpers/pkce';

/**
 * MAU 集計（EcAuthDocs#45）: 同一ユーザーが同月に 2 回ログインしても MAU が 1 のままであることと、
 * マイページの利用状況 API がその数字を返すことを検証する。
 *
 * ユニットテストは SQLite で重複排除を検証しているが、本番と同じ SQL Server の UNIQUE index と
 * `INSERT ... WHERE NOT EXISTS` を通すのはこの spec だけ。筋書きは signup_client_b2b_login.spec.ts と
 * 同じ（申込 → Account トークン → 申込で作られた Client でパスキー登録 → 認証 → /v1/token）で、
 * 認証と交換を 2 回繰り返してから `GET /v1/account/usage` を読む。
 *
 * 実行先の切り替え（E2E_TENANT_BASE_DOMAIN）は signup_client_b2b_login.spec.ts を参照。
 */
test.describe.serial('MAU 集計と利用状況 API', () => {
  const tenantBaseDomain = process.env.E2E_TENANT_BASE_DOMAIN;
  const remote = Boolean(tenantBaseDomain);

  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const accountsHost =
    process.env.E2E_ACCOUNTS_HOST || (remote ? `stg-accounts.${tenantBaseDomain}` : 'accounts.ec-auth.io');
  const accountsPageBaseUrl =
    process.env.E2E_ACCOUNTS_PAGE_URL || (remote ? `https://${accountsHost}` : `https://${accountsHost}:8081`);
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsClientSecret = process.env.ACCOUNTS_CLIENT_SECRET || undefined;
  const accountsRedirectUri =
    process.env.ACCOUNTS_REDIRECT_URI ||
    (remote ? `https://${accountsHost}/auth/callback` : 'https://localhost:8081/auth/callback');

  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const siteHost = `e2e-${runSuffix}.test`;
  const productionSiteUrl = `https://${siteHost}:8081/`;
  const email = `e2e-${runSuffix}@e2e.ec-auth.io`;
  const expectedOrgCode = siteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;
  const tenantApiBaseUrl = remote ? `https://${expectedOrgCode}.${tenantBaseDomain}` : baseUrl;

  // 対象月は JST の暦月（UsageMonth と同じ +09:00 固定）。
  const currentYearMonth = new Date(Date.now() + 9 * 60 * 60 * 1000).toISOString().slice(0, 7);

  let apiAccounts: APIRequestContext;
  let apiTenant: APIRequestContext;
  let mailbox: Mailbox;
  let context: BrowserContext;
  let sitePage: Page;

  let accountAccessToken: string;
  let clientId: string;
  let clientSecret: string;
  let clientDbId: number;
  let registeredRedirectUri: string;
  let registeredRpId: string;
  let b2bAccessToken: string;

  const b2bSubject = randomUUID();
  const externalId = `e2e-admin-${runSuffix}`;

  // 申込は組織コードの重複を弾くためリトライできない（signup_client_b2b_login.spec.ts と同じ）。
  test.describe.configure({ retries: 0 });

  test.beforeAll(async ({ browser }) => {
    console.log(
      `[usage-e2e] mode=${remote ? 'remote' : 'local'} org_code=${expectedOrgCode} email=${email} ` +
        `accounts=${accountsApiBaseUrl} tenant=${tenantApiBaseUrl} year_month=${currentYearMonth}`
    );

    apiAccounts = await request.newContext({
      ignoreHTTPSErrors: true,
      ...(remote ? {} : { extraHTTPHeaders: { Host: accountsHost } }),
    });
    apiTenant = await request.newContext({
      ignoreHTTPSErrors: true,
      ...(remote ? {} : { extraHTTPHeaders: { Host: `${expectedOrgCode}.ec-auth.io` } }),
    });
    mailbox = await createMailbox();

    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();
    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );
    await context.route(`https://${siteHost}/**`, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>shop origin stub</body></html>' })
    );
  });

  test.afterAll(async () => {
    await Promise.allSettled([
      mailbox?.cleanup(email),
      apiAccounts?.dispose(),
      apiTenant?.dispose(),
      context?.close(),
    ]);
    await mailbox?.dispose();
  });

  /** 認証 → 認可コード → /v1/token を 1 回分。access_token を返す。 */
  async function loginOnce(label: string): Promise<string> {
    const { codeVerifier, codeChallenge } = generatePkcePair();
    const auth = await authenticateB2BPasskey({ api: apiTenant, apiBaseUrl: tenantApiBaseUrl, page: sitePage }, {
      clientId,
      rpId: registeredRpId,
      redirectUri: registeredRedirectUri,
      b2bSubject,
      state: `e2e-usage-${label}-${runSuffix}`,
      codeChallenge,
    });
    const code = new URL(auth.redirect_url).searchParams.get('code');
    expect(code, `${label}: authorization code`).toBeTruthy();

    const response = await apiTenant.post(`${tenantApiBaseUrl}/v1/token`, {
      form: {
        client_id: clientId,
        client_secret: clientSecret,
        code: code!,
        redirect_uri: registeredRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
        code_verifier: codeVerifier,
      },
    });
    const body = await response.json();
    if (response.status() !== 200) {
      console.log(`Token error body (${label}):`, JSON.stringify(body));
    }
    expect(response.status(), `${label}: token exchange`).toBe(200);
    expect(body.access_token).toBeTruthy();
    return body.access_token as string;
  }

  async function getUsage(token: string, yearMonth?: string) {
    const query = yearMonth === undefined ? '' : `?year_month=${encodeURIComponent(yearMonth)}`;
    return apiAccounts.get(`${accountsApiBaseUrl}/v1/account/usage${query}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  }

  test('申込 → 確認 → Account トークン取得', async () => {
    test.setTimeout(remote ? 300000 : 90000);

    const result = await signupAndGetAccountToken(apiAccounts, mailbox, context, {
      baseUrl: accountsApiBaseUrl,
      accountsHost,
      accountsPageBaseUrl,
      accountsClientId,
      accountsClientSecret,
      accountsRedirectUri,
      email,
      organizationName: `E2E Usage Org ${runSuffix}`,
      productionSiteUrl,
      ecCubeVersion: '4',
    });

    accountAccessToken = result.accessToken;
    expect(accountAccessToken).toBeTruthy();
  });

  test('ログイン前の利用状況は MAU 0 / 登録ユーザー 0', async () => {
    const client = await fetchSignupClient(apiAccounts, accountsApiBaseUrl, accountAccessToken, expectedOrgCode);
    clientDbId = client.id;
    clientId = client.clientId;
    clientSecret = client.clientSecret;
    registeredRedirectUri = client.redirectUris.find((u) => u.endsWith('/ecauth/callback'))!;
    registeredRpId = client.allowedRpIds[0];

    const response = await getUsage(accountAccessToken);
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.year_month).toBe(currentYearMonth);
    expect(typeof body.as_of).toBe('string');

    const org = body.organizations.find((o: any) => o.code === expectedOrgCode);
    expect(org, 'managed organization is listed').toBeTruthy();
    expect(org.is_billable).toBe(true);
    expect(org.monthly_active_users).toBe(0);
    const usageClient = org.clients.find((c: any) => c.id === clientDbId);
    expect(usageClient, 'client with zero MAU is still listed').toBeTruthy();
    expect(usageClient.client_id).toBe(clientId);
    expect(usageClient.monthly_active_users).toBe(0);
    expect(usageClient.registered_b2b_users).toBe(0);
  });

  test('サイトのオリジンでパスキーを登録する', async () => {
    test.setTimeout(60000);

    sitePage = await context.newPage();
    await sitePage.goto(`https://${siteHost}/`);

    const result = await registerB2BPasskey({ api: apiTenant, apiBaseUrl: tenantApiBaseUrl, page: sitePage }, {
      clientId,
      clientSecret,
      rpId: registeredRpId,
      b2bSubject,
      externalId,
      userName: 'e2e-admin',
      displayName: 'E2E Admin',
      deviceName: 'E2E Test Device',
    });

    expect(result.success).toBe(true);
  });

  test('同一ユーザーが同月に 2 回ログインする', async () => {
    test.setTimeout(120000);

    b2bAccessToken = await loginOnce('first');
    await loginOnce('second');
  });

  test('利用状況 API: MAU は 1 のまま（重複排除）、登録ユーザーは 1', async () => {
    const response = await getUsage(accountAccessToken, currentYearMonth);
    expect(response.status()).toBe(200);
    const body = await response.json();

    const org = body.organizations.find((o: any) => o.code === expectedOrgCode);
    expect(org).toBeTruthy();
    // Organization 単位の distinct（参考値）と Client 単位（請求値）が、Client 1 つなら一致する
    expect(org.monthly_active_users).toBe(1);
    const usageClient = org.clients.find((c: any) => c.id === clientDbId);
    expect(usageClient.monthly_active_users).toBe(1);
    expect(usageClient.registered_b2b_users).toBe(1);
    expect(usageClient.subject_type).toBe('b2b');
  });

  test('year_month の不正値と未来月は 422', async () => {
    const malformed = await getUsage(accountAccessToken, '2026-13');
    expect(malformed.status()).toBe(422);
    const malformedBody = await malformed.json();
    expect(malformedBody.error).toBe('invalid_request');
    expect(malformedBody.field).toBe('year_month');

    const next = new Date(Date.now() + 9 * 60 * 60 * 1000);
    next.setUTCMonth(next.getUTCMonth() + 1);
    const future = await getUsage(accountAccessToken, next.toISOString().slice(0, 7));
    expect(future.status()).toBe(422);
  });

  test('B2B のアクセストークンでは 401', async () => {
    const response = await getUsage(b2bAccessToken);
    expect(response.status()).toBe(401);
    const body = await response.json();
    expect(body.error).toBe('invalid_token');
  });
});
