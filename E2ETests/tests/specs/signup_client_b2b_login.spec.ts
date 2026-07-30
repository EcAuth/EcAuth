import { test, expect, APIRequestContext, BrowserContext, Page, request } from '@playwright/test';
import { randomUUID } from 'crypto';
import { signupAndGetAccountToken } from '../helpers/accounts';
import { registerB2BPasskey, authenticateB2BPasskey } from '../helpers/b2b-passkey';
import { deleteMessages } from '../helpers/mailpit';
import { generatePkcePair } from '../helpers/pkce';

/**
 * 申込で作られた顧客 Client が、**無設定のまま** B2B パスキーログインに使えることを検証する。
 *
 * EcAuth#481 の再発防止。申込が登録する redirect_uri / allowed_rp_ids の初期値と、
 * EC-CUBE プラグインが実際に送る値が噛み合っておらず、新規顧客のパスキーログインが
 * 確定的に失敗していた。既存の E2E は
 *   - b2b_passkey_authentication.spec.ts … シード済み Client を使う
 *   - account_signup_flow.spec.ts        … 申込は通すが accounts コンソール Client で認証する
 * だったため、「申込が作った Client をそのまま使う」経路が誰の担当でもなかった。
 *
 * この spec の肝は **redirect_uri と rp_id をテスト側で組み立てないこと**。
 * どちらも API から取得した登録済みの値（= 申込時の初期値）をそのまま使う。
 *
 * プラグインの実構成に合わせて、2 つの経路を分けて再現する:
 *   - **ブラウザ**は店舗のホスト（= rp_id）で開く。WebAuthn が origin と rp_id の一致を
 *     要求するためで、ページ自体は origin を持ち込むためだけに使う（/healthz を開く）
 *   - **API 呼び出し**は Host: {tenant_name}.ec-auth.io で行う。プラグインは
 *     /platform/v1/client-resolve が返す https://{tenant_name}.ec-auth.io を保存して
 *     そこへ送るため（ClientResolveController:61）。この Host によって EcAuth 側の
 *     グローバルクエリフィルタが顧客 Organization の行に一致する
 *
 * サイトホストに .test を使うのは、RFC 6761 の予約 TLD で公開解決されず、実在ドメインと
 * 衝突しないため。本番 DB に残ってもテストデータであることが明確になる。
 * 申込 URL にポート 8081 を含めることで、redirect_uri にポートが引き継がれることも同時に検証する。
 */
test.describe.serial('申込で作られた Client での B2B パスキーログイン', () => {
  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsRedirectUri = process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';

  // run ごとに一意化して、前回 run の残存データとの衝突を避ける。
  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const siteHost = `e2e-${runSuffix}.test`;
  // 申込 URL。非既定ポートは redirect_uri に引き継がれる（rp_id には含まれない）。
  const productionSiteUrl = `https://${siteHost}:8081/`;
  const email = `e2e-${runSuffix}@e2e.ec-auth.io`;
  // Organization code は host から導出される（[^a-z0-9]+ → '-'）。
  const expectedOrgCode = siteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  let apiAccounts: APIRequestContext; // Host=accounts（申込 API とコンソールのトークン交換用）
  // Host=<tenant_name>.ec-auth.io。プラグインのサーバー側呼び出しを模す。
  // プラグインは /platform/v1/client-resolve が返す https://<tenant_name>.ec-auth.io を
  // ecauth_base_url として保存し、以降の API をそこへ送る（ClientResolveController:61）。
  // Host にテナントが載ることで EcAuth 側のクエリフィルタが顧客 Organization に一致する。
  let apiTenant: APIRequestContext;
  let mailpitCtx: APIRequestContext;
  let context: BrowserContext;
  let sitePage: Page;

  const messageIds: string[] = [];

  // 申込で発行され、API 経由で取得する値。テスト側では一切組み立てない。
  let clientId: string;
  let clientSecret: string;
  let registeredRedirectUri: string;

  const b2bSubject = randomUUID();
  const externalId = `e2e-admin-${runSuffix}`;
  const { codeVerifier, codeChallenge } = generatePkcePair();

  let accessToken: string;
  let authorizationCode: string;

  test.beforeAll(async ({ browser }) => {
    apiAccounts = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { Host: accountsHost },
    });
    apiTenant = await request.newContext({
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { Host: `${expectedOrgCode}.ec-auth.io` },
    });
    mailpitCtx = await request.newContext();

    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();

    // accounts のパスキー登録後の遷移先（マイページ）は E2E 環境に実体が無いので握り潰す。
    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );
  });

  test.afterAll(async () => {
    await deleteMessages(mailpitCtx, messageIds);
    await apiAccounts?.dispose();
    await apiTenant?.dispose();
    await mailpitCtx?.dispose();
    await context?.close();
  });

  test('申込 → 確認 → Account トークン取得', async () => {
    test.setTimeout(90000);

    const result = await signupAndGetAccountToken(apiAccounts, mailpitCtx, context, {
      baseUrl,
      accountsHost,
      accountsPageBaseUrl,
      accountsClientId,
      accountsRedirectUri,
      email,
      organizationName: `E2E Plugin Org ${runSuffix}`,
      productionSiteUrl,
      ecCubeVersion: '4',
    });

    accessToken = result.accessToken;
    messageIds.push(result.messageId);
    expect(accessToken).toBeTruthy();
  });

  test('マイページと同じ経路で client_id / client_secret を取得する', async () => {
    const listResponse = await apiAccounts.get(`${baseUrl}/v1/account/clients`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    expect(listResponse.status()).toBe(200);

    const clients = (await listResponse.json()).clients as Array<{
      id: number;
      client_id: string;
      organization_code: string;
      redirect_uris: string[];
    }>;

    const client = clients.find((c) => c.organization_code === expectedOrgCode);
    expect(client, `organization_code=${expectedOrgCode} の Client が見つかりません`).toBeTruthy();

    clientId = client!.client_id;

    // ---- ここが EcAuth#481 の核心 ----
    // 申込時に登録される redirect_uri は、EC-CUBE 4 系プラグインが送るコールバック URL
    // （{サイトのベース URL}/ecauth/callback）と一致していなければならない。
    // authenticate/verify は完全一致で検証するため、ズレていると確定的に 400 になる。
    expect(client!.redirect_uris).toContain(`https://${siteHost}:8081/ecauth/callback`);
    // 暫定のトップ URL は登録しない（余分な許可を残さない）。
    expect(client!.redirect_uris).not.toContain(`https://${siteHost}:8081/`);

    registeredRedirectUri = client!.redirect_uris.find((u) => u.endsWith('/ecauth/callback'))!;

    const revealResponse = await apiAccounts.post(
      `${baseUrl}/v1/account/clients/${client!.id}/secret/reveal`,
      { headers: { Authorization: `Bearer ${accessToken}` } }
    );
    expect(revealResponse.status()).toBe(200);
    clientSecret = (await revealResponse.json()).client_secret;
    expect(clientSecret).toBeTruthy();
  });

  test('サイトのオリジンでパスキーを登録する', async () => {
    test.setTimeout(60000);

    // WebAuthn は rp_id と origin の一致を要求する。申込サイトのホストで IdP を開き、
    // プラグインが動くのと同じ origin からセレモニーを実行する。
    // /healthz は 200 を返し、テナントにも静的ファイル配信設定にも依存しない。
    sitePage = await context.newPage();
    const response = await sitePage.goto(`https://${siteHost}:8081/healthz`);
    expect(response?.status(), 'サイトホストで IdP に到達できません').toBe(200);

    const result = await registerB2BPasskey({ api: apiTenant, apiBaseUrl: baseUrl, page: sitePage }, {
      clientId,
      clientSecret,
      // rp_id もテスト側で組み立てず、申込サイトのホストをそのまま使う。
      // allowed_rp_ids に含まれていなければサーバー側で弾かれる。
      rpId: siteHost,
      b2bSubject,
      externalId,
      displayName: 'E2E Admin',
      deviceName: 'E2E Test Device',
    });

    expect(result.success).toBe(true);
    expect(typeof result.credential_id).toBe('string');
  });

  test('登録済み redirect_uri のままパスキー認証が通り、認可コードが返る', async () => {
    test.setTimeout(60000);

    const state = `e2e-b2b-${runSuffix}`;
    const result = await authenticateB2BPasskey({ api: apiTenant, apiBaseUrl: baseUrl, page: sitePage }, {
      clientId,
      rpId: siteHost,
      // API から取得した登録済みの値をそのまま使う。組み立てない。
      redirectUri: registeredRedirectUri,
      b2bSubject,
      state,
      codeChallenge,
    });

    expect(result.redirect_url).toBeTruthy();
    const redirectUrl = new URL(result.redirect_url);
    expect(`${redirectUrl.origin}${redirectUrl.pathname}`).toBe(registeredRedirectUri);
    expect(redirectUrl.searchParams.get('state')).toBe(state);

    authorizationCode = redirectUrl.searchParams.get('code')!;
    expect(authorizationCode).toBeTruthy();
  });

  test('認可コードをトークンに交換できる', async () => {
    // トークン交換もプラグインと同じくテナントのホスト宛に行う。
    const response = await apiTenant.post(`${baseUrl}/v1/token`, {
      form: {
        client_id: clientId,
        client_secret: clientSecret,
        code: authorizationCode,
        redirect_uri: registeredRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
        code_verifier: codeVerifier,
      },
    });

    const body = await response.json();
    if (response.status() !== 200) {
      console.log('Token error body:', JSON.stringify(body));
    }
    expect(response.status()).toBe(200);
    expect(body.access_token).toBeTruthy();
    expect(body.id_token).toBeTruthy();

    const idPayload = JSON.parse(Buffer.from(body.id_token.split('.')[1], 'base64url').toString());
    expect(idPayload.sub).toBe(b2bSubject);
  });
});
