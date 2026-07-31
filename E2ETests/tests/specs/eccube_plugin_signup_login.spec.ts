import { test, expect, APIRequestContext, BrowserContext, Page, request } from '@playwright/test';
import { signupAndGetAccountToken, fetchSignupClient } from '../helpers/accounts';
import { createMailbox, Mailbox } from '../helpers/mailbox';
import {
  EcCubeAdmin,
  eccube4Admin,
  eccube2Admin,
  installBrowserDiagnostics,
} from '../helpers/eccube-admin';

/**
 * EC-CUBE のプラグインを実際に動かして、申込から管理画面ログインまでを 1 本で通す。
 *
 * signup_client_b2b_login.spec.ts が EcAuth の API だけで同じ筋書きを検証しているのに対し、
 * こちらは **プラグインの実コードを経由する**。EcAuth#481 のように「申込が登録する初期値」と
 * 「プラグインが実際に送る値」がズレる不具合は、両者を突き合わせないと出ない。
 * API 版で守れるのは EcAuth 側の初期値だけで、プラグインが送る値の方は守れない。
 *
 * 検証する筋書き（顧客が実際に踏む手順そのもの）:
 *   1. 申込 → 確認メール → Account トークン
 *   2. マイページと同じ経路で client_id / client_secret を取得
 *   3. EC-CUBE 管理画面のプラグイン設定に **client_id / client_secret だけ** を入力して保存
 *      → ecauth_base_url は client-resolve で自動解決される（無設定で使えることの検証）
 *      → rp_id も未設定のままにして、リクエストホストが使われることを検証
 *   4. パスキーを登録
 *   5. ログアウト
 *   6. パスキーでログインし、管理画面ホームに到達する
 *
 * 実行には compose.e2e-eccube.yaml で起動した EC-CUBE が必要。ホスト名は
 * E2ETests/scripts/eccube-e2e.sh up が採番して環境変数で渡す。未設定なら skip する。
 */

interface Variant {
  label: string;
  /** 申込フォームの ec_cube_version。登録される redirect_uri の分岐に効く */
  ecCubeVersion: '2' | '4';
  /** 店舗のホスト（= WebAuthn の rp_id）。compose の Caddy が 443 で受ける */
  shopHost: string | undefined;
  /** プラグインが ecauth_base_url として解決するはずのホスト */
  tenantHost: string | undefined;
  /** プラグインが redirect_uri として送るパス */
  callbackPath: string;
  admin: EcCubeAdmin;
}

const VARIANTS: Variant[] = [
  {
    label: 'EC-CUBE 4系',
    ecCubeVersion: '4',
    shopHost: process.env.E2E_ECCUBE4_SHOP_HOST,
    tenantHost: process.env.E2E_ECCUBE4_TENANT_HOST,
    // EcAuthCallbackController の @Route("/ecauth/callback")
    callbackPath: '/ecauth/callback',
    admin: eccube4Admin,
  },
  {
    label: 'EC-CUBE 2系',
    ecCubeVersion: '2',
    shopHost: process.env.E2E_ECCUBE2_SHOP_HOST,
    tenantHost: process.env.E2E_ECCUBE2_TENANT_HOST,
    // LC_Page_EcAuthLogin2_PasskeyApi の HTTPS_URL . 'ecauth/callback.php'
    callbackPath: '/ecauth/callback.php',
    admin: eccube2Admin,
  },
];

const ADMIN_LOGIN_ID = process.env.E2E_ECCUBE_ADMIN_LOGIN_ID || 'admin';
const ADMIN_PASSWORD = process.env.E2E_ECCUBE_ADMIN_PASSWORD || 'password';

for (const variant of VARIANTS) {
  test.describe.serial(`${variant.label}: 申込 → プラグイン設定 → パスキー登録 → 管理画面ログイン`, () => {
    test.skip(
      !variant.shopHost || !variant.tenantHost,
      `${variant.label} の店舗ホストが未設定のためスキップ（E2ETests/scripts/eccube-e2e.sh up で起動してください）`
    );

    // この筋書きはリトライできない。申込は組織コードの重複を弾き
    // （SignupService の organization_already_exists）、組織コードは compose 起動時に
    // 確定した店舗ホストから導出されるため、再実行しても必ず同じコードになる。
    // config の retries（CI では 2）のままだと、後段の失敗がリトライのたびに
    // organization_already_exists に化けて本当の失敗理由が隠れる。
    test.describe.configure({ retries: 0 });

    const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
    const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
    const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;
    const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
    const accountsRedirectUri =
      process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';

    // describe のボディは skip の判定に関係なく収集時に実行される。未設定でも
    // 落ちないようにプレースホルダを置く（この値が実際に使われることは無い）。
    const shopHost = variant.shopHost || 'unconfigured.invalid';
    const tenantHost = variant.tenantHost || 'unconfigured.ec-auth.io';
    // Caddy が 443 で終端するのでポートは付かない。本番と同じ形の URL になる。
    const shopBaseUrl = `https://${shopHost}`;
    // Organization code はサイトホストから導出される（SignupService.DeriveOrganizationCode）。
    const expectedOrgCode = shopHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
    const email = `${expectedOrgCode}@e2e.ec-auth.io`;

    let apiAccounts: APIRequestContext;
    let mailbox: Mailbox;
    let context: BrowserContext;
    let page: Page;

    let accessToken: string;
    let clientId: string;
    let clientSecret: string;

    test.beforeAll(async ({ browser }) => {
      // compose が採番したテナントホストと、申込から導出される組織コードがズレていると
      // プラグインの解決先が Caddy のサイトアドレスに一致せず、原因が分かりにくい失敗になる。
      // 先に突き合わせて、設定ミスをテスト失敗より前に顕在化させる。
      expect(
        `${expectedOrgCode}.ec-auth.io`,
        'compose が採番したテナントホストと申込から導出される組織コードが一致しません'
      ).toBe(tenantHost);

      apiAccounts = await request.newContext({
        ignoreHTTPSErrors: true,
        extraHTTPHeaders: { Host: accountsHost },
      });
      mailbox = await createMailbox();

      context = await browser.newContext({ ignoreHTTPSErrors: true });
      await context.credentials.install();
      await installBrowserDiagnostics(context);

      // accounts のパスキー登録後の遷移先（マイページ）は E2E 環境に実体が無いので握り潰す。
      await context.route(/\/mypage\//, (route) =>
        route.fulfill({
          status: 200,
          contentType: 'text/html',
          body: '<html><body>mypage stub</body></html>',
        })
      );

      page = await context.newPage();
      page.on('console', (msg) => console.log(`[browser:${msg.type()}] ${msg.text()}`));
      page.on('pageerror', (err) => console.log(`[pageerror] ${err.message}`));
    });

    test.afterAll(async () => {
      await mailbox?.cleanup(email);
      await mailbox?.dispose();
      await apiAccounts?.dispose();
      await context?.close();
    });

    test('申込 → 確認 → Account トークン取得', async () => {
      test.setTimeout(120000);

      const result = await signupAndGetAccountToken(apiAccounts, mailbox, context, {
        baseUrl,
        accountsHost,
        accountsPageBaseUrl,
        accountsClientId,
        accountsRedirectUri,
        email,
        organizationName: `E2E ${variant.label} ${expectedOrgCode}`,
        productionSiteUrl: `${shopBaseUrl}/`,
        ecCubeVersion: variant.ecCubeVersion,
      });

      accessToken = result.accessToken;
      expect(accessToken).toBeTruthy();
    });

    test('マイページと同じ経路で client_id / client_secret を取得する', async () => {
      const client = await fetchSignupClient(apiAccounts, baseUrl, accessToken, expectedOrgCode);

      clientId = client.clientId;
      clientSecret = client.clientSecret;
      expect(clientSecret).toBeTruthy();

      // 申込が登録する redirect_uri は、この系のプラグインが送るコールバック URL と
      // 一致していなければならない（authenticate/verify は完全一致で検証する）。
      // ここでは登録値の突き合わせだけを行い、値をテストの後段へは持ち回さない。
      // プラグインは redirect_uri を自分で組み立てて送るため、テスト側が値を渡す余地が無く、
      // 一致しているかどうかは後段のパスキーログインが通るかどうかで現れる。
      expect(client.redirectUris).toContain(`${shopBaseUrl}${variant.callbackPath}`);
    });

    test('プラグイン設定に client_id / client_secret だけを入力して保存する', async () => {
      test.setTimeout(90000);

      await variant.admin.login(page, shopBaseUrl, ADMIN_LOGIN_ID, ADMIN_PASSWORD);
      await variant.admin.saveClientCredentials(page, shopBaseUrl, clientId, clientSecret);

      // 高度な設定を空のまま保存したので、プラグインは /platform/v1/client-resolve で
      // base_url を解決したはず。ここが申込 Organization のテナントを指していないと、
      // 以降の options / verify は既定テナントに解決されて challenge を見失う。
      const resolvedBaseUrl = await variant.admin.readResolvedBaseUrl(page, shopBaseUrl);
      expect(resolvedBaseUrl).toBe(`https://${tenantHost}`);
    });

    test('パスキーを登録する', async () => {
      test.setTimeout(90000);

      const credentialId = await variant.admin.registerPasskey(page, shopBaseUrl, ADMIN_PASSWORD);
      expect(credentialId).toBeTruthy();
    });

    test('ログアウトする', async () => {
      await variant.admin.logout(page, shopBaseUrl);
    });

    test('パスキーでログインし管理画面ホームに到達する', async () => {
      test.setTimeout(90000);

      // ここが通ること自体が、申込が登録した redirect_uri とプラグインが送る値の
      // 一致を意味する。ズレていれば authenticate/verify が 400 を返し、
      // コールバックにも管理画面ホームにも到達しない（EcAuth#481 の再現条件）。
      await variant.admin.passkeyLogin(page, shopBaseUrl);
    });
  });
}
