import { test, expect, BrowserContext, Page, APIRequestContext, request } from '@playwright/test';
import { randomUUID } from 'crypto';
import { waitForMessage, extractToken, deleteMessages } from '../helpers/mailpit';

/**
 * Account 申込フローの E2E テスト（mailpit ベース）。
 *
 * 申込 → 確認メール（mailpit）→ トークン抽出 → /api/signup/confirm → Account 作成 →
 * パスキー登録（既存 B2B 仮想認証器を流用）→ 認証 → 認可コード → /token → managed_orgs 検証。
 *
 * テナント解決:
 *   - /api/signup/* と /token はテナント（accounts）に依存するため Host ヘッダを付与する
 *     （TenantMiddleware は host のセグメント数が 3 以上のときのみ先頭をテナント名にする）。
 *   - パスキー register/authenticate は Client の Organization 経由で解決され（IgnoreQueryFilters）、
 *     リクエストのテナントに依存しない。よってブラウザは localhost のまま rp_id=localhost で動く。
 */
test.describe.serial('Account 申込フローの E2E テスト', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || `${baseUrl}/v1/token`;

  // accounts テナントに解決させる 3 セグメント以上の Host。
  // TenantMiddleware は host セグメント数が 3 以上のときのみ先頭をテナント名にする。
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  // passkey ページの配信元。playwright.config.ts の --host-resolver-rules で
  // accounts.ec-auth.io → 127.0.0.1 にマップ済み。ページの fetch は tenant=accounts に解決され、
  // WebAuthn の rp_id を origin（accountsHost）と一致させられる。
  const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;

  // Account 管理コンソール Client（AccountsOrganizationSeeder が accounts Org に投入）。
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsClientSecret = process.env.ACCOUNTS_CLIENT_SECRET || 'accounts_client_secret';
  // rp_id は passkey ページの origin（accountsHost）と一致させる。accounts Client の
  // AllowedRpIds に accountsHost を含めておく必要がある（ACCOUNTS_ALLOWED_RP_IDS）。
  const accountsRpId = process.env.E2E_ACCOUNTS_RP_ID || accountsHost;
  const accountsRedirectUri = process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';

  // Run ごとに一意化（前回 run の残存データとの衝突を回避）。
  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const email = `e2e-signup-${runSuffix}@example.com`;
  const organizationName = `E2E Signup Org ${runSuffix}`;
  // 顧客 Org の code は site host から導出される（[^a-z0-9]+ → '-'）。一意な host で衝突回避。
  const productionSiteHost = `e2e-${runSuffix}.example.com`;
  const productionSiteUrl = `https://${productionSiteHost}`;
  const expectedOrgCode = productionSiteHost.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  // register/authenticate は external_id フォールバックで既存 Account の B2BUser に解決させる。
  // external_id はサーバー側で ExternalIdHasher.Hash されるため、申込メールと同じ生 email を渡す。
  const b2bSubject = randomUUID();
  const deviceName = 'E2E Signup Device';

  let apiAccounts: APIRequestContext; // Host=accounts を付与した API コンテキスト
  let mailpitCtx: APIRequestContext; // mailpit REST 用（http）
  let context: BrowserContext;
  let page: Page;

  let confirmToken: string;
  let authorizationCode: string;
  const messageIds: string[] = [];

  test.beforeAll(async ({ browser }) => {
    apiAccounts = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { Host: accountsHost },
    });
    mailpitCtx = await request.newContext();

    // passkey ページは accounts テナントの origin で開く（fetch が tenant=accounts に解決され、
    // 申込で作った accounts org の B2BUser を external_id で引き当てられる）。
    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();
    page = await context.newPage();
    await page.goto(`${accountsPageBaseUrl}/b2b-passkey-test.html`);
    await page.waitForLoadState('domcontentloaded');
  });

  test.afterAll(async () => {
    await deleteMessages(mailpitCtx, messageIds);
    await apiAccounts?.dispose();
    await mailpitCtx?.dispose();
    await context?.close();
  });

  test('申込リクエスト（202 Accepted）', async () => {
    const response = await apiAccounts.post(`${baseUrl}/api/signup/request`, {
      data: {
        email,
        organization_name: organizationName,
        contact_name: 'E2E Tester',
        production_site_url: productionSiteUrl,
        ec_cube_version: '4',
      },
    });

    console.log('Signup request status:', response.status());
    if (response.status() !== 202) {
      console.log('Signup request body:', await response.text());
    }
    expect(response.status()).toBe(202);
  });

  test('確認メール受信 → トークン抽出 → confirm（200）', async () => {
    test.setTimeout(30000);

    const message = await waitForMessage(mailpitCtx, email, { subjectIncludes: 'お申し込み確認' });
    messageIds.push(message.ID);
    expect(message.Subject).toContain('お申し込み確認');

    confirmToken = extractToken(message.Text || message.HTML);
    expect(confirmToken.length).toBeGreaterThan(10);

    const response = await apiAccounts.post(`${baseUrl}/api/signup/confirm`, {
      data: { token: confirmToken },
    });

    console.log('Confirm status:', response.status());
    const body = await response.json();
    console.log('Confirm body:', JSON.stringify(body));
    expect(response.status()).toBe(200);
    expect(body.email).toBe(email);
  });

  test('パスキー登録（既存 Account を external_id で解決）', async () => {
    test.setTimeout(30000);

    await page.fill('#clientId', accountsClientId);
    await page.fill('#clientSecret', accountsClientSecret);
    await page.fill('#rpId', accountsRpId);
    await page.fill('#b2bSubject', b2bSubject);
    await page.fill('#externalId', email);
    await page.fill('#redirectUri', accountsRedirectUri);
    await page.fill('#displayName', organizationName);
    await page.fill('#deviceName', deviceName);

    // サーバーが返す timeout=0 を上書き（既存 B2B テストと同じ対処）。
    await page.evaluate(() => {
      const originalCreate = navigator.credentials.create.bind(navigator.credentials);
      navigator.credentials.create = (options?: CredentialCreationOptions) => {
        if (options?.publicKey && (!options.publicKey.timeout || options.publicKey.timeout === 0)) {
          options.publicKey.timeout = 60000;
        }
        return originalCreate(options);
      };
      const originalGet = navigator.credentials.get.bind(navigator.credentials);
      navigator.credentials.get = (options?: CredentialRequestOptions) => {
        if (options?.publicKey && (!options.publicKey.timeout || options.publicKey.timeout === 0)) {
          options.publicKey.timeout = 60000;
        }
        return originalGet(options);
      };
    });

    await page.click('button:has-text("Register New Passkey")');

    const registerResult = page.locator('#registerResult');
    await expect(registerResult).toBeVisible({ timeout: 15000 });

    const resultClass = await registerResult.getAttribute('class');
    if (resultClass?.includes('error')) {
      console.log('Register error:', await registerResult.textContent());
      console.log('Debug log:', (await page.locator('#debugLog').textContent())?.substring(0, 2000));
    }
    await expect(registerResult).toHaveClass(/success/, { timeout: 15000 });
  });

  test('パスキー認証 → 認可コード取得', async () => {
    test.setTimeout(30000);

    await page.fill('#state', `e2e-signup-state-${Date.now()}`);
    await page.click('button:has-text("Authenticate with Passkey")');

    const authenticateResult = page.locator('#authenticateResult');
    await expect(authenticateResult).toBeVisible({ timeout: 15000 });
    await expect(authenticateResult).toHaveClass(/success/, { timeout: 15000 });

    const preContent = await authenticateResult.locator('pre').textContent();
    const responseData = JSON.parse(preContent!);
    const redirectUrl = new URL(responseData.redirect_url);
    authorizationCode = redirectUrl.searchParams.get('code')!;
    expect(authorizationCode).toBeTruthy();
  });

  test('トークン交換 → managed_orgs claim 検証', async () => {
    expect(authorizationCode).toBeTruthy();

    const response = await apiAccounts.post(tokenEndpoint, {
      form: {
        client_id: accountsClientId,
        client_secret: accountsClientSecret,
        code: authorizationCode,
        redirect_uri: accountsRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
      },
    });

    console.log('Token status:', response.status());
    const body = await response.json();
    if (response.status() !== 200) {
      console.log('Token body:', JSON.stringify(body));
    }
    expect(response.status()).toBe(200);
    expect(body.access_token).toBeTruthy();
    expect(body.id_token).toBeTruthy();

    // id_token / access_token の payload をデコードして managed_orgs を検証する。
    const idPayload = JSON.parse(Buffer.from(body.id_token.split('.')[1], 'base64url').toString());
    console.log('id_token payload:', JSON.stringify(idPayload, null, 2));

    const managedOrgs = idPayload.managed_orgs;
    expect(Array.isArray(managedOrgs)).toBe(true);
    expect(managedOrgs.length).toBeGreaterThanOrEqual(1);

    // 申込で作られた顧客 Org が owner ロールで含まれること。
    const owned = managedOrgs.find((o: { code: string }) => o.code === expectedOrgCode);
    expect(owned).toBeTruthy();
    expect(owned.role).toBe('owner');
    expect(typeof owned.org_id).toBe('number');
  });
});
