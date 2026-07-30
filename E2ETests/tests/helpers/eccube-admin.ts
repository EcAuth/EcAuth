import { expect, BrowserContext, Page } from '@playwright/test';

/**
 * EC-CUBE 管理画面の操作を 4 系 / 2 系で共通のインターフェースに揃えるアダプタ。
 *
 * 「申込 → 設定投入 → パスキー登録 → ログイン」という検証したい筋書きは両系で同一で、
 * 違うのは URL とセレクタだけ。spec を 2 本書くと筋書きの差分が埋もれるため、
 * 筋書きは 1 本にまとめ、系ごとの差分をここへ閉じ込める。
 *
 * セレクタはプラグインリポジトリ（ec-cube4-ecauth / ec-cube2-ecauth）のテンプレートに
 * 由来する。プラグイン側の E2E と重複するが、こちらが検証しているのは
 * **申込で払い出された値を無設定のまま使えるか** で、プラグイン側 E2E が
 * ecauth_base_url / rp_id を「高度な設定」で明示指定しているのとは目的が異なる。
 */
export interface EcCubeAdmin {
  /** ID / パスワードで管理画面にログインする */
  login(page: Page, shopBaseUrl: string, loginId: string, password: string): Promise<void>;

  /**
   * プラグイン設定画面で client_id / client_secret **だけ** を保存する。
   * ecauth_base_url と rp_id は空のままにして、プラグイン側の自動解決に委ねる。
   */
  saveClientCredentials(
    page: Page,
    shopBaseUrl: string,
    clientId: string,
    clientSecret: string
  ): Promise<void>;

  /** 保存後の設定画面を開き直し、自動解決された ecauth_base_url を読む */
  readResolvedBaseUrl(page: Page, shopBaseUrl: string): Promise<string>;

  /** パスキーを 1 件登録し、サーバーが返した credential_id を返す */
  registerPasskey(page: Page, shopBaseUrl: string, adminPassword: string): Promise<string>;

  /** ログアウトしてログイン画面に戻る */
  logout(page: Page, shopBaseUrl: string): Promise<void>;

  /** ログイン画面のパスキーボタンからログインし、管理画面ホームまで遷移する */
  passkeyLogin(page: Page, shopBaseUrl: string): Promise<void>;
}

/**
 * WebAuthn 周りの診断とワークアラウンドをブラウザ側に仕込む。
 *
 * プラグインの JS が navigator.credentials を直接呼ぶため、テスト側からは介入できない。
 * 一方でプラグインの catch は NotAllowedError を握り潰すので、失敗しても画面には
 * 「登録に失敗しました」しか出ず CI から原因が追えない。そこで:
 *
 *   - サーバーが timeout=0 を返した場合に有効値へ落とす（0 のままだと環境によって
 *     セレモニーが即座に打ち切られる）
 *   - create / get の解決値と例外名を console に流し、Playwright のログへ届かせる
 *
 * ec-cube4-ecauth / ec-cube2-ecauth の E2E が同じ仕掛けを持っており、その必要性は
 * 両リポジトリの CI で実証済み。
 */
export async function installBrowserDiagnostics(context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    const FALLBACK_TIMEOUT_MS = 60000;

    const originalCreate = navigator.credentials.create.bind(navigator.credentials);
    navigator.credentials.create = async (options?: CredentialCreationOptions) => {
      if (options?.publicKey && !options.publicKey.timeout) {
        options.publicKey.timeout = FALLBACK_TIMEOUT_MS;
      }
      try {
        const cred = await originalCreate(options);
        console.log('[E2E] credentials.create resolved: id=' + (cred as PublicKeyCredential | null)?.id);
        return cred;
      } catch (e) {
        const err = e as Error;
        console.log('[E2E] credentials.create rejected: ' + err.name + ': ' + err.message);
        throw e;
      }
    };

    const originalGet = navigator.credentials.get.bind(navigator.credentials);
    navigator.credentials.get = async (options?: CredentialRequestOptions) => {
      if (options?.publicKey && !options.publicKey.timeout) {
        options.publicKey.timeout = FALLBACK_TIMEOUT_MS;
      }
      try {
        const cred = await originalGet(options);
        console.log('[E2E] credentials.get resolved: id=' + (cred as PublicKeyCredential | null)?.id);
        return cred;
      } catch (e) {
        const err = e as Error;
        console.log('[E2E] credentials.get rejected: ' + err.name + ': ' + err.message);
        throw e;
      }
    };

    window.addEventListener('unhandledrejection', (e) => {
      console.log('[E2E] unhandledrejection: ' + String((e as PromiseRejectionEvent).reason));
    });
  });
}

/**
 * 設定保存が失敗したときに、画面に出ているエラー文言を拾って一行にまとめる。
 * 4 系は Symfony フォームの .invalid-feedback、2 系は設定テンプレートの
 * `<div class="message">`（$arrErr のループ）に出る。
 */
async function describeFormErrors(page: Page): Promise<string> {
  const texts = await page
    .locator('.invalid-feedback, .message, .attention, .alert-danger, .alert-warning')
    .allInnerTexts()
    .catch(() => [] as string[]);
  const visible = texts.map((t) => t.trim()).filter(Boolean);
  return visible.length > 0
    ? `画面のエラー表示: ${visible.join(' / ')}`
    : '画面にエラー表示はありませんでした';
}

/** register/verify のレスポンス。両系とも同じ形を返す。 */
interface RegisterVerifyBody {
  success: boolean;
  credential_id: string;
}

/**
 * 登録ボタン押下から register/verify の応答までを待つ共通処理。
 *
 * パスキー一覧はセッションに access_token が入るまで空表示になる（access_token は
 * パスキーログイン成功後に初めて入る）ため、画面ではなくサーバーの応答で登録完了を判定する。
 */
async function awaitRegisterVerify(
  page: Page,
  urlFragment: string,
  clickConfirm: () => Promise<void>
): Promise<string> {
  const verifyPromise = page.waitForResponse(
    (res) => res.url().includes(urlFragment) && res.request().method() === 'POST',
    { timeout: 30000 }
  );
  await clickConfirm();
  const response = await verifyPromise;

  const text = await response.text();
  if (response.status() !== 200) {
    throw new Error(`${urlFragment} が ${response.status()} を返しました: ${text}`);
  }
  const body = JSON.parse(text) as RegisterVerifyBody;
  expect(body.success, `register/verify が success=false を返しました: ${text}`).toBe(true);
  expect(typeof body.credential_id).toBe('string');
  return body.credential_id;
}

// ---------------------------------------------------------------------------
// EC-CUBE 4 系
// ---------------------------------------------------------------------------

const ADVANCED_TOGGLE_4 =
  'button[data-bs-toggle="collapse"][data-bs-target="#ecauth-advanced-settings"]';

export const eccube4Admin: EcCubeAdmin = {
  async login(page, shopBaseUrl, loginId, password) {
    await page.goto(`${shopBaseUrl}/admin/login`);
    await page.fill('input[name="login_id"]', loginId);
    await page.fill('input[name="password"]', password);
    await Promise.all([
      page.waitForURL((url) => /^\/admin\/?$/.test(new URL(url).pathname), { timeout: 30000 }),
      page.click('button[type="submit"]'),
    ]);
  },

  async saveClientCredentials(page, shopBaseUrl, clientId, clientSecret) {
    await page.goto(`${shopBaseUrl}/admin/ecauth_login43/config`);
    await page.fill('input[name="config[client_id]"]', clientId);
    await page.fill('input[name="config[client_secret]"]', clientSecret);
    // 「高度な設定」（ecauth_base_url / rp_id）は開かない。空のまま保存することで、
    // ConfigController が client-resolve を叩いて base_url を埋める経路を通す。
    await page.click('button[type="submit"]');
    try {
      await expect(page.locator('.alert-success')).toBeVisible({ timeout: 30000 });
    } catch (e) {
      // 失敗の主因は client-resolve の失敗で、その理由はフォームのエラー欄にしか出ない。
      // タイムアウトだけでは原因が分からないので添えて投げ直す。
      throw new Error(`${await describeFormErrors(page)} / 元エラー: ${(e as Error).message}`);
    }
  },

  async readResolvedBaseUrl(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}/admin/ecauth_login43/config`);
    await page.click(ADVANCED_TOGGLE_4);
    await expect(page.locator('#ecauth-advanced-settings')).toHaveClass(/show/);
    return page.inputValue('input[name="config[ecauth_base_url]"]');
  },

  async registerPasskey(page, shopBaseUrl, adminPassword) {
    await page.goto(`${shopBaseUrl}/admin/ecauth/passkey/`);
    await expect(page.locator('.card-header:has-text("登録済みパスキー")')).toBeVisible();

    await page.click('#ecauth-passkey-add');
    await expect(page.locator('#ecauth-password-modal')).toBeVisible();
    await page.fill('#ecauth-password-input', adminPassword);

    return awaitRegisterVerify(page, '/ecauth/passkey/register/verify', () =>
      page.click('#ecauth-password-confirm')
    );
  },

  async logout(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}/admin/logout`);
    await expect(page).toHaveURL(/\/admin\/login/);
    await expect(page.locator('input[name="login_id"]')).toBeVisible();
  },

  async passkeyLogin(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}/admin/login`);
    const passkeyBtn = page.locator('#ecauth-passkey-login');
    await expect(passkeyBtn).toBeVisible();

    // options → assertion → verify → redirect_url → /ecauth/callback → /admin/
    await Promise.all([
      page.waitForURL((url) => /^\/admin\/?$/.test(new URL(url).pathname), { timeout: 30000 }),
      passkeyBtn.click(),
    ]);
    await expect(page.locator('input[name="login_id"]')).toHaveCount(0);
    await expect(page.locator('h2', { hasText: 'ホーム' })).toBeVisible();
  },
};

// ---------------------------------------------------------------------------
// EC-CUBE 2 系
// ---------------------------------------------------------------------------

/**
 * ADMIN_DIR は前後スラッシュ付き（既定 '/admin/'）。EC-CUBE 2 は管理画面 URL を
 * 秘匿するためインストール時に変更できるので、ハードコードせず env から読む。
 */
const ADMIN_BASE_2 = process.env.E2E_ECCUBE2_ADMIN_BASE || '/admin/';
const ADMIN_BASE_2_RE = ADMIN_BASE_2.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
/** 2 系のプラグイン設定は「オーナーズストア > プラグイン」からのポップアップだが、直リンクでも同じ画面が出る */
const PLUGIN_ID_2 = process.env.E2E_ECCUBE2_PLUGIN_ID || '10000';

export const eccube2Admin: EcCubeAdmin = {
  async login(page, shopBaseUrl, loginId, password) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}`);
    await page.fill('input[name="login_id"]', loginId);
    await page.fill('input[name="password"]', password);
    await Promise.all([
      page.waitForURL(new RegExp(`${ADMIN_BASE_2_RE}home\\.php`), { timeout: 30000 }),
      page.click('a:has-text("LOGIN")'),
    ]);
  },

  async saveClientCredentials(page, shopBaseUrl, clientId, clientSecret) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}load_plugin_config.php?plugin_id=${PLUGIN_ID_2}`);
    await page.fill('input[name="client_id"]', clientId);
    await page.fill('input[name="client_secret"]', clientSecret);
    // ecauth_base_url / rp_id は空のまま。4 系と同じく自動解決の経路を通す。

    // 保存に成功すると tpl_onload の alert("設定を保存しました。") が出る。
    // 失敗した場合はダイアログではなく画面上のエラー欄に出るため、待ち切れなかったら
    // その内容を添えて投げる（client-resolve 失敗がここに現れる）。
    const dialogMessage = page.waitForEvent('dialog', { timeout: 30000 }).then(async (dialog) => {
      const message = dialog.message();
      await dialog.accept().catch(() => {});
      return message;
    });
    await page.click('button:has-text("登録")');

    let message: string;
    try {
      message = await dialogMessage;
    } catch (e) {
      throw new Error(
        `設定保存の完了ダイアログが出ませんでした。${await describeFormErrors(page)}` +
          ` / 元エラー: ${(e as Error).message}`
      );
    }
    expect(message).toContain('設定を保存しました');
    await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});
  },

  async readResolvedBaseUrl(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}load_plugin_config.php?plugin_id=${PLUGIN_ID_2}`);
    return page.inputValue('input[name="ecauth_base_url"]');
  },

  async registerPasskey(page, shopBaseUrl, adminPassword) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}ecauth/passkey.php`);
    await expect(page.locator('span', { hasText: '登録済みパスキー' })).toBeVisible();

    await page.click('#ecauth-passkey-add');
    await expect(page.locator('#ecauth-password-modal')).toBeVisible();
    await page.fill('#ecauth-password-input', adminPassword);

    // 登録成功時の alert → reload が走るので、後続の goto と競合しないよう
    // ダイアログを受け流してから networkidle を待つ。
    page.on('dialog', (dialog) => {
      dialog.accept().catch(() => {});
    });

    const credentialId = await awaitRegisterVerify(
      page,
      `${ADMIN_BASE_2}ecauth/api/register-verify.php`,
      () => page.click('#ecauth-password-confirm')
    );
    await page.waitForLoadState('networkidle', { timeout: 10000 }).catch(() => {});
    return credentialId;
  },

  async logout(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}logout.php`);
    await expect(page).toHaveURL(new RegExp(`${ADMIN_BASE_2_RE.replace(/\/$/, '')}/?$`));
    await expect(page.locator('input[name="login_id"]')).toBeVisible();
  },

  async passkeyLogin(page, shopBaseUrl) {
    await page.goto(`${shopBaseUrl}${ADMIN_BASE_2}`);
    const passkeyBtn = page.locator('#ecauth-passkey-login');
    await expect(passkeyBtn).toBeVisible();

    // options → assertion → verify → redirect_url → /ecauth/callback.php → <ADMIN_BASE>home.php
    await Promise.all([
      page.waitForURL(new RegExp(`${ADMIN_BASE_2_RE}home\\.php`), { timeout: 30000 }),
      passkeyBtn.click(),
    ]);
    await expect(page.locator('input[name="login_id"]')).toHaveCount(0);
    await expect(page.locator('h1, h2', { hasText: 'ホーム' })).toBeVisible();
  },
};
