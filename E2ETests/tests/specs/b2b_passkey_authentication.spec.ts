import { test, expect, BrowserContext, Page, CDPSession, request } from '@playwright/test';

test.describe.serial('B2Bパスキー認証フローのE2Eテスト', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || `${baseUrl}/token`;
  const clientId = process.env.DEFAULT_CLIENT_ID || 'client_id';
  const clientSecret = process.env.DEFAULT_CLIENT_SECRET || 'client_secret';
  const rpId = process.env.DEV_B2B_ALLOWED_RP_IDS || 'localhost';
  const b2bSubject = process.env.DEV_B2B_USER_SUBJECT || '3f7c0ab4-b004-4102-b6ed-a730369dd237';
  const redirectUri = process.env.DEV_B2B_REDIRECT_URI || 'https://localhost:8081/admin/ecauth/callback';

  let context: BrowserContext;
  let page: Page;
  let cdpSession: CDPSession;

  // テスト間で共有するデータ
  let authorizationCode: string;
  let accessToken: string;
  let credentialId: string;

  let authenticatorId: string;

  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    page = await context.newPage();

    // ページを先に開いてから CDP セッションを作成
    await page.goto(`${baseUrl}/b2b-passkey-test.html`);
    await page.waitForLoadState('domcontentloaded');

    // CDP Virtual Authenticator セットアップ
    cdpSession = await page.context().newCDPSession(page);
    await cdpSession.send('WebAuthn.enable');
    const result = await cdpSession.send('WebAuthn.addVirtualAuthenticator', {
      options: {
        protocol: 'ctap2',
        hasUserVerification: true,
        automaticPresenceSimulation: true,
        isUserVerified: true,
        hasResidentKey: true,
        transport: 'internal',
      },
    });
    authenticatorId = result.authenticatorId;

    console.log('Virtual Authenticator configured, id:', authenticatorId);
  });

  test.afterAll(async () => {
    await cdpSession?.detach();
    await context?.close();
  });

  test('パスキー登録', async () => {
    test.setTimeout(30000);

    // フォーム値を設定（beforeAll で既にページを開いている）
    await page.fill('#clientId', clientId);
    await page.fill('#clientSecret', clientSecret);
    await page.fill('#rpId', rpId);
    await page.fill('#b2bSubject', b2bSubject);
    await page.fill('#displayName', 'Test Admin');
    await page.fill('#deviceName', 'E2E Test Device');

    // サーバーから返される timeout=0 を上書きするため、
    // navigator.credentials.create をラップして timeout を設定
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

    // パスキー登録ボタンをクリック
    await page.click('button:has-text("Register New Passkey")');

    // 結果を待つ
    const registerResult = page.locator('#registerResult');
    await expect(registerResult).toBeVisible({ timeout: 15000 });

    // エラーの場合はメッセージを出力してデバッグ
    const resultClass = await registerResult.getAttribute('class');
    if (resultClass?.includes('error')) {
      const errorText = await registerResult.textContent();
      const debugLog = await page.locator('#debugLog').textContent();
      console.log('Registration error:', errorText);
      console.log('Debug log:', debugLog?.substring(0, 2000));
    }

    await expect(registerResult).toHaveClass(/success/, { timeout: 15000 });

    const resultText = await registerResult.textContent();
    console.log('Registration result:', resultText);
    expect(resultText).toContain('Success');
  });

  test('パスキー認証', async () => {
    test.setTimeout(30000);

    // state を生成
    const state = 'e2e-test-state-' + Date.now();
    await page.fill('#state', state);

    // パスキー認証ボタンをクリック
    await page.click('button:has-text("Authenticate with Passkey")');

    // 成功メッセージを待つ
    const authenticateResult = page.locator('#authenticateResult');
    await expect(authenticateResult).toBeVisible({ timeout: 15000 });
    await expect(authenticateResult).toHaveClass(/success/, { timeout: 15000 });

    // redirect_url から code を取得
    const resultText = await authenticateResult.textContent();
    console.log('Authentication result:', resultText);

    // レスポンスJSONからredirect_urlを抽出
    const resultHtml = await authenticateResult.innerHTML();
    const preContent = await authenticateResult.locator('pre').textContent();
    expect(preContent).toBeTruthy();

    const responseData = JSON.parse(preContent!);
    expect(responseData.redirect_url).toBeTruthy();

    const redirectUrl = new URL(responseData.redirect_url);
    authorizationCode = redirectUrl.searchParams.get('code')!;
    expect(authorizationCode).toBeTruthy();
    console.log('Authorization code obtained:', authorizationCode.substring(0, 20) + '...');
  });

  test('トークン交換', async () => {
    expect(authorizationCode).toBeTruthy();

    const tokenRequest = await request.newContext({
      ignoreHTTPSErrors: true,
    });

    const response = await tokenRequest.post(tokenEndpoint, {
      form: {
        client_id: clientId,
        client_secret: clientSecret,
        code: authorizationCode,
        redirect_uri: redirectUri,
        grant_type: 'authorization_code',
        scope: 'openid b2b',
      },
    });

    console.log('Token response status:', response.status());
    const responseBody = await response.json();
    console.log('Token response:', JSON.stringify(responseBody, null, 2));

    expect(response.status()).toBe(200);
    expect(responseBody.access_token).toBeTruthy();
    expect(responseBody.token_type).toBe('Bearer');
    expect(responseBody.id_token).toBeTruthy();

    accessToken = responseBody.access_token;
    console.log('Access token obtained');
  });

  test('UserInfoエンドポイント検証', async () => {
    expect(accessToken).toBeTruthy();

    const apiContext = await request.newContext({
      ignoreHTTPSErrors: true,
    });

    const response = await apiContext.get(`${baseUrl}/userinfo`, {
      headers: {
        Authorization: `Bearer ${accessToken}`,
      },
    });

    console.log('UserInfo response status:', response.status());
    const responseBody = await response.json();
    console.log('UserInfo response:', JSON.stringify(responseBody, null, 2));

    expect(response.status()).toBe(200);
    expect(responseBody.sub).toBe(b2bSubject);
  });

  test('パスキー一覧取得', async () => {
    expect(accessToken).toBeTruthy();

    const apiContext = await request.newContext({
      ignoreHTTPSErrors: true,
    });

    const response = await apiContext.get(`${baseUrl}/b2b/passkey/list`, {
      headers: {
        Authorization: `Bearer ${accessToken}`,
      },
    });

    console.log('List response status:', response.status());
    const responseBody = await response.json();
    console.log('List response:', JSON.stringify(responseBody, null, 2));

    expect(response.status()).toBe(200);
    expect(responseBody.passkeys).toBeTruthy();
    expect(responseBody.passkeys.length).toBe(1);

    credentialId = responseBody.passkeys[0].credential_id;
    expect(credentialId).toBeTruthy();
    console.log('Credential ID:', credentialId);
  });

  test('パスキー削除', async () => {
    expect(accessToken).toBeTruthy();
    expect(credentialId).toBeTruthy();

    const apiContext = await request.newContext({
      ignoreHTTPSErrors: true,
    });

    const response = await apiContext.delete(
      `${baseUrl}/b2b/passkey/${encodeURIComponent(credentialId)}`,
      {
        headers: {
          Authorization: `Bearer ${accessToken}`,
        },
      }
    );

    console.log('Delete response status:', response.status());
    expect(response.status()).toBe(204);
  });

  test('削除後の一覧確認', async () => {
    expect(accessToken).toBeTruthy();

    const apiContext = await request.newContext({
      ignoreHTTPSErrors: true,
    });

    const response = await apiContext.get(`${baseUrl}/b2b/passkey/list`, {
      headers: {
        Authorization: `Bearer ${accessToken}`,
      },
    });

    console.log('List after delete status:', response.status());
    const responseBody = await response.json();
    console.log('List after delete:', JSON.stringify(responseBody, null, 2));

    expect(response.status()).toBe(200);
    expect(responseBody.passkeys).toBeTruthy();
    expect(responseBody.passkeys.length).toBe(0);
  });
});
