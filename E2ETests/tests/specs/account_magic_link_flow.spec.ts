import { test, expect, APIRequestContext, request } from '@playwright/test';
import { waitForMessage, deleteMessages, extractToken } from '../helpers/mailpit';

/**
 * マジックリンクログインの E2E テスト（mailpit ベース）。
 *
 * 事前に申込 → confirm で Account を作成し、
 * request → メール（mailpit）→ リンク抽出 → verify → 認可コード → /token → managed_orgs を検証。
 * さらに異常系（second-use / レート制限）も検証する。
 *
 * マジックリンクは UI を介さない純粋な API フローのため、テナント（accounts）解決は
 * すべて Host ヘッダで行う（パスキーのようなブラウザ操作は不要）。
 */
test.describe.serial('マジックリンクログインの E2E テスト', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || `${baseUrl}/v1/token`;
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsClientSecret = process.env.ACCOUNTS_CLIENT_SECRET || 'accounts_client_secret';
  const accountsRedirectUri = process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';

  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const email = `e2e-magic-${runSuffix}@example.com`;
  const productionSiteHost = `magic-${runSuffix}.example.com`;
  const productionSiteUrl = `https://${productionSiteHost}`;
  const expectedOrgCode = productionSiteHost.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  const signupSubject = 'お申し込み確認';
  const magicLinkSubject = 'ログインリンク';

  let apiAccounts: APIRequestContext;
  let mailpitCtx: APIRequestContext;

  // 後始末で削除するメッセージ ID（他 spec のメールを消さないよう ID 指定で削除）。
  const messageIds: string[] = [];
  // 消費テスト用に保持するログイントークン。
  let magicToken: string;

  test.beforeAll(async () => {
    apiAccounts = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { Host: accountsHost },
    });
    mailpitCtx = await request.newContext();

    // 前提: マジックリンク対象の Account を申込フローで作成する。
    const signupRes = await apiAccounts.post(`${baseUrl}/api/signup/request`, {
      data: {
        email,
        organization_name: `E2E Magic Org ${runSuffix}`,
        contact_name: 'E2E Tester',
        production_site_url: productionSiteUrl,
        ec_cube_version: '4',
      },
    });
    expect(signupRes.status()).toBe(202);

    const confirmMail = await waitForMessage(mailpitCtx, email, { subjectIncludes: signupSubject });
    messageIds.push(confirmMail.ID);
    const confirmToken = extractToken(confirmMail.Text || confirmMail.HTML);

    const confirmRes = await apiAccounts.post(`${baseUrl}/api/signup/confirm`, {
      data: { token: confirmToken },
    });
    expect(confirmRes.status()).toBe(200);
  });

  test.afterAll(async () => {
    await deleteMessages(mailpitCtx, messageIds);
    await apiAccounts?.dispose();
    await mailpitCtx?.dispose();
  });

  test('マジックリンク要求 → メール → verify → 認可コード → トークン + managed_orgs', async () => {
    test.setTimeout(30000);

    const reqRes = await apiAccounts.post(`${baseUrl}/api/account/magic-link/request`, {
      data: { email },
    });
    // Email enumeration 対策により、常に 200 を返す。
    expect(reqRes.status()).toBe(200);

    const mail = await waitForMessage(mailpitCtx, email, { subjectIncludes: magicLinkSubject });
    messageIds.push(mail.ID);
    magicToken = extractToken(mail.Text || mail.HTML);
    expect(magicToken.length).toBeGreaterThan(10);

    const verifyRes = await apiAccounts.post(`${baseUrl}/api/account/magic-link/verify`, {
      data: { token: magicToken },
    });
    console.log('Magic-link verify status:', verifyRes.status());
    const verifyBody = await verifyRes.json();
    if (verifyRes.status() !== 200) {
      console.log('Magic-link verify body:', JSON.stringify(verifyBody));
    }
    expect(verifyRes.status()).toBe(200);
    expect(verifyBody.location).toBeTruthy();

    const location = new URL(verifyBody.location);
    const code = location.searchParams.get('code')!;
    expect(code).toBeTruthy();

    const tokenRes = await apiAccounts.post(tokenEndpoint, {
      form: {
        client_id: accountsClientId,
        client_secret: accountsClientSecret,
        code,
        redirect_uri: accountsRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
      },
    });
    console.log('Token status:', tokenRes.status());
    const tokenBody = await tokenRes.json();
    if (tokenRes.status() !== 200) {
      console.log('Token body:', JSON.stringify(tokenBody));
    }
    expect(tokenRes.status()).toBe(200);
    expect(tokenBody.access_token).toBeTruthy();
    expect(tokenBody.id_token).toBeTruthy();

    const idPayload = JSON.parse(Buffer.from(tokenBody.id_token.split('.')[1], 'base64url').toString());
    const managedOrgs = idPayload.managed_orgs;
    expect(Array.isArray(managedOrgs)).toBe(true);
    const owned = managedOrgs.find((o: { code: string }) => o.code === expectedOrgCode);
    expect(owned).toBeTruthy();
    expect(owned.role).toBe('owner');
  });

  test('single-use: 消費済みトークンの再 verify は失敗する', async () => {
    expect(magicToken).toBeTruthy();

    const res = await apiAccounts.post(`${baseUrl}/api/account/magic-link/verify`, {
      data: { token: magicToken },
    });
    console.log('Second-use verify status:', res.status());
    // Compare-And-Set により消費済みトークンは再利用不可（200 にはならない）。
    expect(res.status()).not.toBe(200);
  });

  test('rate limit: 直近要求済みの同一メールへの再要求は 429', async () => {
    const res = await apiAccounts.post(`${baseUrl}/api/account/magic-link/request`, {
      data: { email },
    });
    console.log('Rate-limited request status:', res.status());
    // 同一メールは 5 分に 1 回の制限。beforeAll 直後の happy path で 1 回要求済みのため 429。
    expect(res.status()).toBe(429);
  });
});
