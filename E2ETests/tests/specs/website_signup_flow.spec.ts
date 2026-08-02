import { test, expect, BrowserContext, Page } from '@playwright/test';
import { createMailbox, extractToken, Mailbox } from '../helpers/mailbox';

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
 *     → マイページの編集 UI から redirect_uri / allowed_rp_ids を実 API で全置換
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

  let mailbox: Mailbox;
  let context: BrowserContext;
  let page: Page;

  let confirmToken: string;

  test.beforeAll(async ({ browser }) => {
    // 本 spec は全経路をブラウザ（フロント）から通すため、API 直叩き用のコンテキストは持たない。
    // 確認メールの本文だけは受信口から読む必要がある。
    mailbox = await createMailbox();

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
    // 後始末は互いに独立させる（cleanup の失敗で dispose / close を落とさない）。
    await Promise.allSettled([mailbox?.cleanup(email), context?.close()]);
    await mailbox?.dispose();
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

    const message = await mailbox.waitForMessage(email, { subjectIncludes: 'お申し込み確認' });

    // Signup:ConfirmBaseUrl がフロントに向いていること（バックエンド→フロントの URL 契約）。
    const body = message.text || message.html;
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

  /**
   * マイページの Client 設定セクション（ecauth-website の mypage.js が data-section で出す）。
   */
  function settingsSection(key: 'redirect_uris' | 'allowed_rp_ids') {
    return page
      .locator('.client-item')
      .filter({ hasText: expectedOrgCode })
      .locator(`.ci-settings[data-section="${key}"]`);
  }

  /**
   * 編集 UI（EcAuth/ecauth-website#19）を含まない ref を配信している場合に、そのケースだけ
   * 明示理由付きでスキップする。CI は既定で ecauth-website のデフォルトブランチを checkout
   * するため、website 側が未マージの間はセクションごと存在しない。
   */
  async function skipUnlessSettingsUi() {
    const present = await settingsSection('redirect_uris').count();
    test.skip(
      present === 0,
      'ecauth-website の ref に Client 設定の編集 UI がありません（EcAuth/ecauth-website#19 未マージ）。'
        + ' workflow_dispatch の website_ref に該当ブランチを指定すると検証できます。'
    );
  }

  /** 畳まれていれば開いて返す（reload すると details は閉じた状態に戻る）。 */
  async function openSettings(key: 'redirect_uris' | 'allowed_rp_ids') {
    const section = settingsSection(key);
    await expect(section).toHaveCount(1, { timeout: 15000 });
    if (!(await section.locator('.row-list').isVisible())) {
      await section.locator('summary').click();
    }
    await expect(section.locator('.row-list')).toBeVisible();
    return section;
  }

  /** 設定セクションの入力欄の値を表示順に取り出す。 */
  function inputValuesOf(section: ReturnType<typeof settingsSection>): Promise<string[]> {
    return section.locator('.row-input').evaluateAll((els) => els.map((e) => (e as HTMLInputElement).value));
  }

  test('マイページの編集 UI から redirect_uri を追加でき、実 API 側に残る', async () => {
    test.setTimeout(45000);
    await skipUnlessSettingsUi();

    const section = await openSettings('redirect_uris');

    // 申込が作る初期値は EC-CUBE 4 系のコールバック URL。プラグインが authenticate/verify に
    // 送る値と一致していることを、API 直叩きではなく画面越しに確認する。
    expect(await inputValuesOf(section)).toEqual([`https://${productionSiteHost}/ecauth/callback`]);

    const added = `https://${productionSiteHost}/shop/ecauth/callback`;
    await section.locator('.row-add').click();
    await section.locator('.row-input').nth(1).fill(added);
    await section.getByRole('button', { name: '保存' }).click();

    await expect(section.locator('[data-status="section"]')).toHaveClass(/ok/, { timeout: 15000 });
    await expect(section.locator('.ci-count')).toHaveText('2 件');

    // 画面の状態ではなく、取り直した一覧で永続化を確認する。
    await page.reload();
    const reloaded = await openSettings('redirect_uris');
    // redirect_uri の取得に ORDER BY は無いため、順序ではなく集合で比較する。
    expect((await inputValuesOf(reloaded)).slice().sort()).toEqual(
      [`https://${productionSiteHost}/ecauth/callback`, added].sort()
    );
  });

  test('RP ID の編集は確認ダイアログを経て保存され、サーバ側で正規化される', async () => {
    test.setTimeout(45000);
    await skipUnlessSettingsUi();

    const section = await openSettings('allowed_rp_ids');
    expect(await inputValuesOf(section)).toEqual([productionSiteHost]);

    // 大文字で入れてもサーバが小文字（Punycode）に正規化して返す。
    page.once('dialog', (dialog) => dialog.accept());
    await section.locator('.row-input').nth(0).fill(productionSiteHost.toUpperCase());
    await section.getByRole('button', { name: '保存' }).click();

    await expect(section.locator('[data-status="section"]')).toHaveClass(/ok/, { timeout: 15000 });
    expect(await inputValuesOf(section)).toEqual([productionSiteHost]);

    await page.reload();
    const reloaded = await openSettings('allowed_rp_ids');
    expect(await inputValuesOf(reloaded)).toEqual([productionSiteHost]);
  });

  test('実 API の 422 が入力値を含まない理由付きでフロントに表示される', async () => {
    test.setTimeout(45000);
    await skipUnlessSettingsUi();

    const section = await openSettings('redirect_uris');

    // https 必須。サーバは入力値をエラーに載せず「N 件目」という位置だけを返す
    // （redirect_uri は user:pass@ を含みうるため）。フロントの行番号と対応することまで見る。
    await section.locator('.row-input').nth(0).fill(`http://${productionSiteHost}/ecauth/callback`);
    await section.getByRole('button', { name: '保存' }).click();

    const status = section.locator('[data-status="section"]');
    await expect(status).toHaveClass(/err/, { timeout: 15000 });
    await expect(status).toContainText('1 件目');
    await expect(status).toContainText('https://');
    // 入力値そのものは反映されない（ログ・エラーレポートに資格情報を残さないため）。
    await expect(status).not.toContainText(productionSiteHost);

    // 失敗しても入力は保持され、直して再送できる状態のままであること。
    expect(await inputValuesOf(section)).toContain(`http://${productionSiteHost}/ecauth/callback`);
    await section.getByRole('button', { name: '取り消し' }).click();
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

    const mail = await mailbox.waitForMessage(email, { subjectIncludes: 'ログインリンク' });

    // MagicLink:BaseUrl がフロントに向いていること。
    const body = mail.text || mail.html;
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
