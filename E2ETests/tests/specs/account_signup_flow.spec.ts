import { test, expect, BrowserContext, Page, APIRequestContext, request } from '@playwright/test';
import { waitForMessage, extractToken, deleteMessages } from '../helpers/mailpit';
import { generatePkcePair } from '../helpers/pkce';

/**
 * Account 申込フローの E2E テスト（mailpit ベース）。
 *
 * 申込 → 確認メール（mailpit）→ トークン抽出 → /api/signup/confirm → Account 作成 ＋ 登録トークン発行 →
 * パスキー登録（/passkey/register）→ パスキー認証（/passkey/authenticate）→ 認可コード →
 * /token（PKCE）→ managed_orgs 検証。
 *
 * 本 spec は **本番と同じ Razor ページ**（/passkey/register・/passkey/authenticate）を通す。
 * 旧実装は wwwroot/b2b-passkey-test.html（テスト専用ページ・PKCE 非対応）を叩いていたため、
 * マイページが実際に通る経路を検証できていなかった。
 *
 * PKCE:
 *   管理コンソール Client は public client（ACCOUNTS_CLIENT_PUBLIC=true → client_secret 無し）
 *   のため、/token は PKCE 必須。認可コードへの code_challenge 束縛は authenticate/verify で行われる。
 *
 * テナント解決:
 *   - /api/signup/* と /token はテナント（accounts）に依存するため Host ヘッダを付与する
 *     （TenantMiddleware は host のセグメント数が 3 以上のときのみ先頭をテナント名にする）。
 *   - パスキーページは accounts テナントの origin で開く。ページ内 JS は rp_id に
 *     window.location.hostname を使うため、origin と rp_id が自動的に一致する。
 */
test.describe.serial('Account 申込フローの E2E テスト', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || `${baseUrl}/v1/token`;

  // accounts テナントに解決させる 3 セグメント以上の Host。
  // TenantMiddleware は host セグメント数が 3 以上のときのみ先頭をテナント名にする。
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  // パスキーページの配信元。playwright.config.ts の --host-resolver-rules で
  // accounts.ec-auth.io → 127.0.0.1 にマップ済み。ページの fetch は tenant=accounts に解決され、
  // WebAuthn の rp_id を origin（accountsHost）と一致させられる。
  const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;

  // Account 管理コンソール Client（AccountsOrganizationSeeder が accounts Org に投入）。
  // public client のため client_secret は持たない。
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsRedirectUri = process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';

  // Run ごとに一意化（前回 run の残存データとの衝突を回避）。
  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const email = `e2e-signup-${runSuffix}@example.com`;
  const organizationName = `E2E Signup Org ${runSuffix}`;
  // 顧客 Org の code は site host から導出される（[^a-z0-9]+ → '-'）。一意な host で衝突回避。
  const productionSiteHost = `e2e-${runSuffix}.example.com`;
  const productionSiteUrl = `https://${productionSiteHost}`;
  const expectedOrgCode = productionSiteHost.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  let apiAccounts: APIRequestContext; // Host=accounts を付与した API コンテキスト
  let mailpitCtx: APIRequestContext; // mailpit REST 用（http）
  let context: BrowserContext;
  let page: Page;

  let confirmToken: string;
  let registrationToken: string;
  let authorizationCode: string;
  const messageIds: string[] = [];

  // PKCE。authenticate/verify で認可コードに challenge を束縛し、/token で verifier を提示する。
  const { codeVerifier, codeChallenge } = generatePkcePair();

  test.beforeAll(async ({ browser }) => {
    apiAccounts = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { Host: accountsHost },
    });
    mailpitCtx = await request.newContext();

    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();

    // サーバーが返す timeout=0 を上書き（既存 B2B テストと同じ対処）。
    // ページ遷移をまたぐため addInitScript で全ページに適用する。
    await context.addInitScript(() => {
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

    // マイページ（フロント）は E2E 環境に存在しないため、到達を検知できるよう握り潰す。
    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );

    page = await context.newPage();
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

  test('確認メール受信 → トークン抽出 → confirm（200・登録トークン発行）', async () => {
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
    expect(response.status()).toBe(200);
    expect(body.email).toBe(email);

    // 登録トークンは client_secret の代替として register/options・verify を認可する。
    // public client 化により、パスキー登録で client_secret を使う経路は存在しない。
    registrationToken = body.registration_token;
    expect(registrationToken).toBeTruthy();
  });

  test('パスキー登録（/passkey/register・登録トークンで認可）', async () => {
    test.setTimeout(30000);

    // 登録トークンはフラグメントで渡す（サーバへ送信されずアクセスログに残らない）。
    const registerUrl = `${accountsPageBaseUrl}/passkey/register`
      + `?client_id=${encodeURIComponent(accountsClientId)}`
      + `&email=${encodeURIComponent(email)}`
      + `#token=${encodeURIComponent(registrationToken)}`;

    await page.goto(registerUrl);
    await page.waitForLoadState('domcontentloaded');

    await page.click('#reg-btn');

    const status = page.locator('#status');
    await expect(status).toBeVisible({ timeout: 15000 });

    // 失敗時は原因をログに出してから落とす。
    const statusClass = await status.getAttribute('class');
    if (statusClass?.includes('err')) {
      console.log('Register error:', await status.textContent());
    }

    // 登録成功後、ページは自動でマイページへ遷移する（route で stub 済み）。
    await page.waitForURL(new RegExp('/mypage/'), { timeout: 15000 });
  });

  test('パスキー認証（/passkey/authenticate・PKCE）→ 認可コード取得', async () => {
    test.setTimeout(30000);

    const clientState = `e2e-signup-state-${runSuffix}`;
    const authenticateUrl = `${accountsPageBaseUrl}/passkey/authenticate`
      + `?client_id=${encodeURIComponent(accountsClientId)}`
      + `&redirect_uri=${encodeURIComponent(accountsRedirectUri)}`
      + `&code_challenge=${encodeURIComponent(codeChallenge)}`
      + `&code_challenge_method=S256`
      + `&state=${encodeURIComponent(clientState)}`;

    await page.goto(authenticateUrl);
    await page.waitForLoadState('domcontentloaded');

    await page.click('#auth-btn');

    const status = page.locator('#status');
    await expect(status).toBeVisible({ timeout: 15000 });
    const statusClass = await status.getAttribute('class');
    if (statusClass?.includes('err')) {
      console.log('Authenticate error:', await status.textContent());
    }

    // 認証成功でサーバーが生成した redirect_url へ遷移する。
    // redirect_uri（/auth/callback）は IdP 側に実体が無く 404 になるが、
    // ナビゲーション自体は完了するため URL から認可コードを取り出せる。
    const escapedRedirectUri = accountsRedirectUri.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await page.waitForURL(new RegExp(escapedRedirectUri + '\\?code='), { timeout: 15000 });

    const url = new URL(page.url());
    authorizationCode = url.searchParams.get('code')!;
    expect(authorizationCode).toBeTruthy();
    // RFC 6749 Section 4.1.2: クライアントの state がそのまま返ること
    expect(url.searchParams.get('state')).toBe(clientState);
  });

  test('トークン交換（PKCE・client_secret 無し）→ managed_orgs claim 検証', async () => {
    expect(authorizationCode).toBeTruthy();

    const response = await apiAccounts.post(tokenEndpoint, {
      form: {
        client_id: accountsClientId,
        code: authorizationCode,
        redirect_uri: accountsRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
        code_verifier: codeVerifier,
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
