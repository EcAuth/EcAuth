import { test, expect, APIRequestContext, BrowserContext, Page, request } from '@playwright/test';
import { randomUUID } from 'crypto';
import { signupAndGetAccountToken, fetchSignupClient } from '../helpers/accounts';
import { registerB2BPasskey, authenticateB2BPasskey } from '../helpers/b2b-passkey';
import { createMailbox, Mailbox } from '../helpers/mailbox';
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
 *     要求するためで、ページ自体は origin を持ち込むためだけに使う（中身は stub）
 *   - **API 呼び出し**は {tenant_name} のホスト宛に行う。プラグインは
 *     /platform/v1/client-resolve が返す https://{tenant_name}.ec-auth.io を保存して
 *     そこへ送るため（ClientResolveController:61）。このホストによって EcAuth 側の
 *     グローバルクエリフィルタが顧客 Organization の行に一致する
 *
 * サイトホストに .test を使うのは、RFC 6761 の予約 TLD で公開解決されず、実在ドメインと
 * 衝突しないため。DB に残ってもテストデータであることが明確になる。
 * 申込 URL にポート 8081 を含めることで、redirect_uri にポートが引き継がれることも同時に検証する。
 *
 * ## 実行先の切り替え
 *
 * 既定はローカル Docker（1 つの IdP に Host ヘッダでテナントを解決させる）。
 * E2E_TENANT_BASE_DOMAIN を設定すると **デプロイ済み環境モード** になり、テナント解決を
 * Host ヘッダの差し替えではなく実ホスト名で行う（Cloudflare 配下では SNI と Host の
 * 不一致を避ける必要があり、オリジンへの直アクセスも許可 IP で塞がれているため）。
 * 本番向けの設定値は .github/workflows/production.yml の verify ジョブを参照。
 */
test.describe.serial('申込で作られた Client での B2B パスキーログイン', () => {
  // 設定されていればデプロイ済み環境モード（例: ec-auth.io）。
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

  // run ごとに一意化して、前回 run の残存データとの衝突を避ける。
  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const siteHost = `e2e-${runSuffix}.test`;
  // 申込 URL。非既定ポートは redirect_uri に引き継がれる（rp_id には含まれない）。
  const productionSiteUrl = `https://${siteHost}:8081/`;
  const email = `e2e-${runSuffix}@e2e.ec-auth.io`;
  // Organization code は host から導出される（[^a-z0-9]+ → '-'）。
  const expectedOrgCode = siteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  // 申込 API / トークン交換の宛先。ローカルは 1 つの IdP に Host ヘッダでテナントを解決させる。
  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;
  // プラグインのサーバー側呼び出しの宛先。プラグインは /platform/v1/client-resolve が返す
  // https://{tenant_name}.ec-auth.io を ecauth_base_url として保存し、以降の API をそこへ送る。
  const tenantApiBaseUrl = remote ? `https://${expectedOrgCode}.${tenantBaseDomain}` : baseUrl;

  let apiAccounts: APIRequestContext; // accounts テナント（申込 API とコンソールのトークン交換用）
  let apiTenant: APIRequestContext; // 顧客テナント（プラグインのサーバー側呼び出しを模す）
  let mailbox: Mailbox;
  let context: BrowserContext;
  let sitePage: Page;

  // 申込で発行され、API 経由で取得する値。テスト側では一切組み立てない。
  let clientId: string;
  let clientSecret: string;
  let clientDbId: number;
  let registeredRedirectUri: string;
  let registeredRpId: string;

  const b2bSubject = randomUUID();
  const externalId = `e2e-admin-${runSuffix}`;
  const { codeVerifier, codeChallenge } = generatePkcePair();

  let accessToken: string;
  let authorizationCode: string;

  // この筋書きはリトライできない。申込は組織コードの重複を弾き
  // （SignupService の organization_already_exists）、組織コードは収集時に確定する
  // siteHost から導出されるため、リトライしても必ず同じコードになる。
  // config の retries（CI では 2）のままだと、後段の失敗がリトライのたびに
  // organization_already_exists に化けて本当の失敗理由が隠れる。
  test.describe.configure({ retries: 0 });

  test.beforeAll(async ({ browser }) => {
    // 作られた Organization はデプロイ済み環境では DB に残る（クリーンアップは EcAuth#487）。
    // 後から特定できるよう、run ごとの識別子をログに残す。
    console.log(
      `[signup-smoke] mode=${remote ? 'remote' : 'local'} org_code=${expectedOrgCode} email=${email} ` +
        `accounts=${accountsApiBaseUrl} tenant=${tenantApiBaseUrl}`
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

    // accounts のパスキー登録後の遷移先（マイページ）は E2E 環境に実体が無いので握り潰す。
    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );

    // 疑似サイトのオリジンは WebAuthn の origin を持ち込むためだけに使うので、中身は要らない。
    // 実体を持たせないことで実行先に依存しなくなる: .test は公開解決されず、
    // デプロイ済み環境ではオリジンへの直アクセスも Cloudflare の許可 IP で塞がれている。
    await context.route(`https://${siteHost}/**`, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>shop origin stub</body></html>' })
    );
  });

  test.afterAll(async () => {
    // 後始末は互いに独立させる。cleanup はデプロイ済み環境ではネットワーク越し
    // （Cloudflare Worker）になるため、ここが投げると以降の dispose / close が
    // 丸ごとスキップされ、CI ワーカーにリソースが残る。
    await Promise.allSettled([
      mailbox?.cleanup(email),
      apiAccounts?.dispose(),
      apiTenant?.dispose(),
      context?.close(),
    ]);
    await mailbox?.dispose();
  });

  test('申込 → 確認 → Account トークン取得', async () => {
    // デプロイ済み環境では SendGrid 送信 → Inbound Parse → Workers KV（結果整合）を
    // 経るため、確認メールが読めるまでローカルより時間がかかる。
    test.setTimeout(remote ? 300000 : 90000);

    const result = await signupAndGetAccountToken(apiAccounts, mailbox, context, {
      baseUrl: accountsApiBaseUrl,
      accountsHost,
      accountsPageBaseUrl,
      accountsClientId,
      accountsClientSecret,
      accountsRedirectUri,
      email,
      organizationName: `E2E Plugin Org ${runSuffix}`,
      productionSiteUrl,
      ecCubeVersion: '4',
    });

    accessToken = result.accessToken;
    expect(accessToken).toBeTruthy();
  });

  test('マイページと同じ経路で client_id / client_secret を取得する', async () => {
    const client = await fetchSignupClient(apiAccounts, accountsApiBaseUrl, accessToken, expectedOrgCode);

    clientDbId = client.id;
    clientId = client.clientId;
    clientSecret = client.clientSecret;
    expect(clientSecret).toBeTruthy();

    // ---- ここが EcAuth#481 の核心 ----
    // 申込時に登録される redirect_uri は、EC-CUBE 4 系プラグインが送るコールバック URL
    // （{サイトのベース URL}/ecauth/callback）と一致していなければならない。
    // authenticate/verify は完全一致で検証するため、ズレていると確定的に 400 になる。
    expect(client.redirectUris).toContain(`https://${siteHost}:8081/ecauth/callback`);
    // 暫定のトップ URL は登録しない（余分な許可を残さない）。
    expect(client.redirectUris).not.toContain(`https://${siteHost}:8081/`);

    // rp_id 側も同じ突き合わせを行う。ブラウザが動く origin（= siteHost）が
    // allowed_rp_ids に無ければ、options の時点でサーバーに弾かれる。
    // 申込 URL は www 無しなので、登録されるのはこの 1 件だけ（www 付きなら除去版も入る）。
    expect(client.allowedRpIds).toEqual([siteHost]);

    registeredRedirectUri = client.redirectUris.find((u) => u.endsWith('/ecauth/callback'))!;
    registeredRpId = client.allowedRpIds[0];
  });

  test('サイトのオリジンでパスキーを登録する', async () => {
    test.setTimeout(60000);

    // WebAuthn は rp_id と origin の一致を要求する。申込サイトのホストでページを開き、
    // プラグインが動くのと同じ origin からセレモニーを実行する（中身は beforeAll の stub）。
    sitePage = await context.newPage();
    await sitePage.goto(`https://${siteHost}/`);

    const result = await registerB2BPasskey({ api: apiTenant, apiBaseUrl: tenantApiBaseUrl, page: sitePage }, {
      clientId,
      clientSecret,
      // rp_id もテスト側で組み立てず、API から取得した登録済みの値をそのまま使う。
      // ブラウザは https://{siteHost} で開いているので、登録値がそことズレていれば
      // WebAuthn の origin 検証で落ちる。
      rpId: registeredRpId,
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
    const result = await authenticateB2BPasskey({ api: apiTenant, apiBaseUrl: tenantApiBaseUrl, page: sitePage }, {
      clientId,
      // redirect_uri / rp_id とも API から取得した登録済みの値をそのまま使う。組み立てない。
      rpId: registeredRpId,
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
    const response = await apiTenant.post(`${tenantApiBaseUrl}/v1/token`, {
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

  /**
   * 申込の初期値が実利用と合わなかった場合（EcAuth#481 が起きた状況）に、顧客がマイページから
   * 自分で直せることを検証する。ここが無いと復旧手段がサポート経由の DB 直接操作しかない。
   */
  test('マイページから redirect_uri / allowed_rp_ids を更新でき、更新後の値で認証が通る', async () => {
    test.setTimeout(60000);

    const headers = { Authorization: `Bearer ${accessToken}` };
    // サブディレクトリへ移設した想定の新しいコールバック URL。
    const updatedRedirectUri = `https://${siteHost}:8081/shop/ecauth/callback`;

    const uriResponse = await apiAccounts.post(
      `${accountsApiBaseUrl}/v1/account/clients/${clientDbId}/redirect-uris`,
      { headers, data: { redirect_uris: [updatedRedirectUri] } }
    );
    expect(uriResponse.status(), await uriResponse.text()).toBe(200);
    expect((await uriResponse.json()).redirect_uris).toEqual([updatedRedirectUri]);

    // rp_id は登録済みパスキーが束縛されているので消さず、www 付きを足すだけにする。
    const rpIdResponse = await apiAccounts.post(
      `${accountsApiBaseUrl}/v1/account/clients/${clientDbId}/allowed-rp-ids`,
      { headers, data: { allowed_rp_ids: [registeredRpId, `www.${registeredRpId}`] } }
    );
    expect(rpIdResponse.status(), await rpIdResponse.text()).toBe(200);

    // マイページを開き直したときに見える値としても反映されていること。
    const refreshed = await fetchSignupClient(apiAccounts, accountsApiBaseUrl, accessToken, expectedOrgCode);
    expect(refreshed.redirectUris).toEqual([updatedRedirectUri]);
    expect(refreshed.allowedRpIds).toEqual([registeredRpId, `www.${registeredRpId}`]);

    // 更新後の redirect_uri で認証が通る = 完全一致検証が新しい値を見ている。
    // 逆に旧値はもう通らない（全置換なので許可リストから消えている）。
    const state = `e2e-b2b-updated-${runSuffix}`;
    const result = await authenticateB2BPasskey({ api: apiTenant, apiBaseUrl: tenantApiBaseUrl, page: sitePage }, {
      clientId,
      rpId: registeredRpId,
      redirectUri: updatedRedirectUri,
      b2bSubject,
      state,
      codeChallenge,
    });

    const redirectUrl = new URL(result.redirect_url);
    expect(`${redirectUrl.origin}${redirectUrl.pathname}`).toBe(updatedRedirectUri);
    expect(redirectUrl.searchParams.get('code')).toBeTruthy();
  });
});
