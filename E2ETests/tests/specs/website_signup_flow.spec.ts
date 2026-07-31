import { test, expect, BrowserContext, Page, APIRequestContext, request } from '@playwright/test';
import { waitForMessage, deleteMessages } from '../helpers/mailpit';
import { extractToken } from '../helpers/mailbox';

/**
 * ecauth-website（ec-auth.io）のフロントを実バックエンドに通すフル結合 E2E。
 *
 * ecauth-website リポジトリ側の E2E（e2e/tests/*.spec.ts）は API を page.route() で
 * スタブするため高速に回るが、**実 API との契約齟齬・実 CORS・実 WebAuthn** は検出できない。
 * 本 spec がその層を担当する:
 *
 *   確認メール URL がフロントを指すか（Signup:ConfirmBaseUrl の配線）
 *     → /signup/confirm/ で確定（クロスオリジン CORS）
 *     → accounts の /passkey/register で実パスキー登録（登録トークンをフラグメントで受け渡し）
 *     → Frontend:BaseUrl 経由でマイページへ復帰
 *     → /mypage/ から PKCE で認可開始 → accounts で実パスキー認証 → 認可コード
 *     → /auth/callback が /v1/token でトークン交換（public client・PKCE）
 *     → /v1/account/clients で Client 一覧、secret の reveal / 再生成
 *     → リカバリ（マジックリンク）でも同じマイページに着地する
 *
 * 前提（CI では playwright.yml が用意する）:
 *   - IdentityProvider が https://localhost:8081 で稼働し、accounts.ec-auth.io に解決すること
 *   - ecauth-website を hugo server --tlsAuto で E2E_WEBSITE_BASE_URL に配信していること
 *   - サーバ側が以下を website のオリジンに向けて配線していること
 *       Signup__AllowedOrigins__0 / Frontend__BaseUrl /
 *       Signup__ConfirmBaseUrl__accounts / MagicLink__BaseUrl__accounts / ACCOUNTS_REDIRECT_URI
 *
 * ホスト名解決は playwright.config.ts の --host-resolver-rules が
 * ec-auth.io / accounts.ec-auth.io をいずれも 127.0.0.1 に向ける。
 */

/** フロントの配信元。未設定ならこの spec 全体をスキップする（他リポジトリの成果物に依存するため）。 */
const WEBSITE_BASE = process.env.E2E_WEBSITE_BASE_URL;

test.describe.serial('ecauth-website フロント × EcAuth 実バックエンドの結合 E2E', () => {
  test.skip(
    !WEBSITE_BASE,
    'E2E_WEBSITE_BASE_URL が未設定のためスキップ（ecauth-website を hugo server で配信し、'
      + 'サーバ側の Frontend__BaseUrl / Signup__AllowedOrigins をそのオリジンに合わせて起動すること）'
  );

  const websiteBase = (WEBSITE_BASE ?? '').replace(/\/$/, '');
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;

  // run ごとに一意化（前回 run の残存データとの衝突を回避）。
  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const email = `e2e-website-${runSuffix}@example.com`;
  const productionSiteHost = `web-${runSuffix}.example.com`;
  const expectedOrgCode = productionSiteHost.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  let mailpitCtx: APIRequestContext;
  let context: BrowserContext;
  let page: Page;

  let confirmToken: string;
  const messageIds: string[] = [];

  test.beforeAll(async ({ browser }) => {
    // 本 spec は全経路をブラウザ（フロント）から通すため、API 直叩き用のコンテキストは持たない。
    // mailpit だけは REST でメール本文を読む必要がある。
    mailpitCtx = await request.newContext();

    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();

    // サーバーが返す timeout=0 を上書きする（既存 B2B / Account spec と同じ対処）。
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

    page = await context.newPage();
    // 失敗時の切り分けのため、フロント JS の例外とコンソールエラーを拾う。
    page.on('pageerror', (e) => console.log('[pageerror]', e.message));
    page.on('console', (msg) => {
      if (msg.type() === 'error') {
        console.log('[console.error]', msg.text());
      }
    });
  });

  test.afterAll(async () => {
    await deleteMessages(mailpitCtx, messageIds);
    await mailpitCtx?.dispose();
    await context?.close();
  });

  test('申込フォーム（/signup/）から実 API に申し込め、確認メールの URL がフロントを指す', async () => {
    test.setTimeout(30000);

    await page.goto(`${websiteBase}/signup/`);
    await page.fill('#email', email);
    await page.fill('#org', `E2E Website Org ${runSuffix}`);
    await page.fill('#contact', 'E2E Tester');
    await page.fill('#prod', `https://${productionSiteHost}`);
    await page.locator('input[name="ec_cube_version"][value="4"]').check();
    await page.click('#submit-btn');

    // クロスオリジン（ec-auth.io → accounts.ec-auth.io）の CORS もここで通る。
    const status = page.locator('#status');
    await expect(status).toHaveClass(/ok/, { timeout: 15000 });
    await expect(status).toContainText('確認メールを送信しました');

    const message = await waitForMessage(mailpitCtx, email, { subjectIncludes: 'お申し込み確認' });
    messageIds.push(message.ID);

    // Signup:ConfirmBaseUrl がフロントに向いていること（バックエンド→フロントの URL 契約）。
    const body = message.Text || message.HTML;
    expect(body).toContain(`${websiteBase}/signup/confirm?token=`);

    confirmToken = extractToken(body);
    expect(confirmToken.length).toBeGreaterThan(10);
  });

  test('確認ページ（/signup/confirm/）が実 API で申込を確定し、パスキー登録へ誘導する', async () => {
    test.setTimeout(30000);

    // バックエンドはスラッシュ無しの URL を発行する。静的配信側が
    // クエリを保持したまま /signup/confirm/ へリダイレクトすること自体も検証対象。
    await page.goto(`${websiteBase}/signup/confirm?token=${encodeURIComponent(confirmToken)}`);
    await expect(page).toHaveURL(/\/signup\/confirm\/\?token=/);

    await page.click('#confirm-btn');

    const status = page.locator('#status');
    await expect(status).toHaveClass(/ok/, { timeout: 15000 });
    await expect(status).toContainText(email);
    await expect(page.locator('#next-step')).toBeVisible();
  });

  test('accounts（/passkey/register）で実パスキーを登録し、マイページへ復帰する', async () => {
    test.setTimeout(30000);

    await page.click('#passkey-btn');
    await page.waitForURL(new RegExp('/passkey/register'), { timeout: 15000 });

    // 登録トークンはフラグメントで渡っている（クエリには載らない）。
    const registerUrl = new URL(page.url());
    expect(registerUrl.origin).toBe(accountsPageBaseUrl);
    expect(registerUrl.search).not.toContain('token=');
    expect(new URLSearchParams(registerUrl.hash.replace(/^#/, '')).get('token')).toBeTruthy();

    await page.click('#reg-btn');

    const status = page.locator('#status');
    await expect(status).toBeVisible({ timeout: 15000 });
    const statusClass = await status.getAttribute('class');
    if (statusClass?.includes('err')) {
      console.log('Register error:', await status.textContent());
    }

    // Frontend:BaseUrl の配線どおり、フロントのマイページへ戻る。
    await page.waitForURL(`${websiteBase}/mypage/`, { timeout: 15000 });
    // 登録直後はまだアクセストークンが無いため、ログイン導線が出る。
    await expect(page.locator('#login-view')).toBeVisible();
  });

  test('マイページから PKCE で認可を開始し、パスキー認証 → トークン交換まで通る', async () => {
    test.setTimeout(45000);

    await page.click('#login-btn');
    await page.waitForURL(new RegExp('/passkey/authenticate'), { timeout: 15000 });

    // マイページが組み立てた認可リクエスト（PKCE 必須）。
    const authorizeUrl = new URL(page.url());
    expect(authorizeUrl.origin).toBe(accountsPageBaseUrl);
    expect(authorizeUrl.searchParams.get('code_challenge_method')).toBe('S256');
    expect(authorizeUrl.searchParams.get('code_challenge')).toBeTruthy();
    expect(authorizeUrl.searchParams.get('state')).toBeTruthy();
    expect(authorizeUrl.searchParams.get('redirect_uri')).toBe(`${websiteBase}/auth/callback`);

    await page.click('#auth-btn');

    const status = page.locator('#status');
    await expect(status).toBeVisible({ timeout: 15000 });
    const statusClass = await status.getAttribute('class');
    if (statusClass?.includes('err')) {
      console.log('Authenticate error:', await status.textContent());
    }

    // 認可コードは /auth/callback（フロント）へ返り、auth-callback.js が /v1/token で交換して
    // マイページへ遷移する。中間の callback で止まらずマイページまで到達することを見る。
    await page.waitForURL(`${websiteBase}/mypage/`, { timeout: 20000 });
    await expect(page.locator('#app-view')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('#login-view')).toBeHidden();
  });

  test('マイページが実 API から Client 一覧を取得して表示する', async () => {
    test.setTimeout(30000);

    // 申込で作られた顧客 Org の Client が出ること（組織コードはサイト host から導出される）。
    const item = page.locator('.client-item').filter({ hasText: expectedOrgCode });
    await expect(item).toHaveCount(1, { timeout: 15000 });

    const idRow = item.locator('.secret-row').filter({ hasText: 'Client ID' });
    await expect(idRow.locator('code')).not.toBeEmpty();

    // 一覧では secret を返さないため、既定はマスク表示。
    const secretRow = item.locator('.secret-row').filter({ hasText: 'Client Secret' });
    await expect(secretRow.locator('code')).toHaveText('•'.repeat(16));
  });

  test('client_secret を reveal し、再生成すると値が変わる', async () => {
    test.setTimeout(30000);

    const item = page.locator('.client-item').filter({ hasText: expectedOrgCode });
    const secretRow = item.locator('.secret-row').filter({ hasText: 'Client Secret' });

    await secretRow.getByRole('button', { name: '表示' }).click();
    await expect(secretRow.getByRole('button', { name: '隠す' })).toBeVisible({ timeout: 15000 });

    const revealed = (await secretRow.locator('code').textContent())?.trim() ?? '';
    // 暗号化保存された secret が復号されて返ること（マスクでも空でもない）。
    expect(revealed.length).toBeGreaterThan(10);
    expect(revealed).not.toBe('•'.repeat(16));

    page.once('dialog', (dialog) => dialog.accept());
    await secretRow.getByRole('button', { name: '再生成' }).click();

    await expect(page.locator('#list-status')).toHaveClass(/ok/, { timeout: 15000 });
    const regenerated = (await secretRow.locator('code').textContent())?.trim() ?? '';
    expect(regenerated.length).toBeGreaterThan(10);
    expect(regenerated).not.toBe(revealed);
  });

  test('リカバリ: /signin/ からマジックリンクを要求し、マイページに着地する', async () => {
    test.setTimeout(45000);

    // パスキーで得たトークンを捨て、未ログイン状態から始める。
    await page.goto(`${websiteBase}/mypage/`);
    await page.click('#logout-link');
    await expect(page.locator('#login-view')).toBeVisible();

    await page.goto(`${websiteBase}/signin/`);
    await page.fill('#email', email);
    await page.click('#submit-btn');
    await expect(page.locator('#status')).toHaveClass(/ok/, { timeout: 15000 });

    const mail = await waitForMessage(mailpitCtx, email, { subjectIncludes: 'ログインリンク' });
    messageIds.push(mail.ID);

    // MagicLink:BaseUrl がフロントに向いていること。
    const body = mail.Text || mail.HTML;
    expect(body).toContain(`${websiteBase}/signin/magic-link?token=`);
    const magicToken = extractToken(body);

    await page.goto(`${websiteBase}/signin/magic-link?token=${encodeURIComponent(magicToken)}`);
    await expect(page).toHaveURL(/\/signin\/magic-link\/\?token=/);
    await page.click('#login-btn');

    // verify は認可コードではなくトークンを直接返すため /auth/callback を経由しない。
    await page.waitForURL(`${websiteBase}/mypage/`, { timeout: 20000 });
    await expect(page.locator('#app-view')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('.client-item').filter({ hasText: expectedOrgCode })).toHaveCount(1);
  });

  test('申込フォームのサーバ側バリデーションがフロントに正しく伝わる', async () => {
    test.setTimeout(30000);

    // 確認済みのドメインで再申込すると organization_already_exists になる
    // （request 段階の EnsureOrganizationCodesAvailableAsync は 422 を返す）。
    // サーバの field 指定に応じてフロントが該当入力欄を invalid にすることまで見る。
    await page.goto(`${websiteBase}/signup/`);
    await page.fill('#email', `e2e-website-dup-${runSuffix}@example.com`);
    await page.fill('#org', `E2E Dup Org ${runSuffix}`);
    await page.fill('#contact', 'E2E Tester');
    await page.fill('#prod', `https://${productionSiteHost}`);
    await page.click('#submit-btn');

    const status = page.locator('#status');
    await expect(status).toHaveClass(/err/, { timeout: 15000 });
    await expect(status).toContainText('このドメインは既に EcAuth に登録されています');
    await expect(page.locator('#f-prod')).toHaveClass(/invalid/);
  });
});
