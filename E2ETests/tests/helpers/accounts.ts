import { expect, APIRequestContext, BrowserContext, Page } from '@playwright/test';
import { waitForMessage, extractToken } from './mailpit';
import { generatePkcePair } from './pkce';

/**
 * Account の申込からアクセストークン取得までを一括で行うヘルパ。
 *
 * 「申込で作られた顧客 Client が実際に使えるか」を検証する spec では、client_id /
 * client_secret を **DB から直接読まず、マイページと同じ経路**（Account トークン →
 * GET /v1/account/clients → POST .../secret/reveal）で取得したい。その前段にあたる
 * 申込〜トークン取得をここにまとめる。
 *
 * account_signup_flow.spec.ts と手順は重複するが、役割が違うので統合しない:
 *   - account_signup_flow.spec.ts は各ステップを個別の test に分け、申込フロー自体を検証する
 *   - こちらは他の spec の前提条件を整えるフィクスチャ
 */

export interface SignupOptions {
  /** IdP のベース URL（既定 https://localhost:8081） */
  baseUrl: string;
  /** accounts テナントに解決させる Host ヘッダ */
  accountsHost: string;
  /** accounts テナントのページ配信元（origin と rp_id を一致させる） */
  accountsPageBaseUrl: string;
  /** 管理コンソール Client（public client） */
  accountsClientId: string;
  accountsRedirectUri: string;

  email: string;
  organizationName: string;
  /** 申込するサイトの URL。ここから Organization code / rp_id / redirect_uri が導出される */
  productionSiteUrl: string;
  /** "2" | "4" | "other" */
  ecCubeVersion: string;
}

export interface SignupResult {
  /** SubjectType=Account のアクセストークン。/v1/account/* の認可に使う */
  accessToken: string;
  /** mailpit 上の確認メール ID（後始末用） */
  messageId: string;
}

/**
 * 申込 → 確認メール → confirm → パスキー登録 → パスキー認証 → トークン交換 を通し、
 * Account のアクセストークンを返す。
 *
 * 前提:
 *   - context に context.credentials.install() 済みの仮想オーセンティケータがあること
 *   - playwright.config.ts の --host-resolver-rules で accountsHost が解決できること
 */
export async function signupAndGetAccountToken(
  api: APIRequestContext,
  mailpit: APIRequestContext,
  context: BrowserContext,
  options: SignupOptions
): Promise<SignupResult> {
  const { codeVerifier, codeChallenge } = generatePkcePair();

  // --- 申込 ---
  const requestResponse = await api.post(`${options.baseUrl}/api/signup/request`, {
    data: {
      email: options.email,
      organization_name: options.organizationName,
      contact_name: 'E2E Tester',
      production_site_url: options.productionSiteUrl,
      ec_cube_version: options.ecCubeVersion,
    },
  });
  if (requestResponse.status() !== 202) {
    throw new Error(`申込に失敗しました (${requestResponse.status()}): ${await requestResponse.text()}`);
  }

  // --- 確認メール → confirm ---
  const message = await waitForMessage(mailpit, options.email, { subjectIncludes: 'お申し込み確認' });
  const confirmToken = extractToken(message.Text || message.HTML);

  const confirmResponse = await api.post(`${options.baseUrl}/api/signup/confirm`, {
    data: { token: confirmToken },
  });
  if (confirmResponse.status() !== 200) {
    throw new Error(`confirm に失敗しました (${confirmResponse.status()}): ${await confirmResponse.text()}`);
  }
  const registrationToken = (await confirmResponse.json()).registration_token as string;
  expect(registrationToken).toBeTruthy();

  // --- accounts のパスキー登録 ---
  // 登録トークンはフラグメントで渡す（サーバへ送信されずアクセスログに残らない）。
  const page: Page = await context.newPage();
  try {
    await page.goto(
      `${options.accountsPageBaseUrl}/passkey/register` +
        `?client_id=${encodeURIComponent(options.accountsClientId)}` +
        `&email=${encodeURIComponent(options.email)}` +
        `#token=${encodeURIComponent(registrationToken)}`
    );
    await page.waitForLoadState('domcontentloaded');
    // 登録に成功するとページはマイページへ自動遷移する。ステータス表示は途中経過でも
    // 可視になるため、遷移の完了を成功判定に使う。
    await clickAndWaitForUrl(page, '#reg-btn', /\/mypage\//, 'accounts のパスキー登録');

    // --- accounts のパスキー認証（PKCE）→ 認可コード ---
    const state = `e2e-accounts-${Date.now()}`;
    await page.goto(
      `${options.accountsPageBaseUrl}/passkey/authenticate` +
        `?client_id=${encodeURIComponent(options.accountsClientId)}` +
        `&redirect_uri=${encodeURIComponent(options.accountsRedirectUri)}` +
        `&code_challenge=${encodeURIComponent(codeChallenge)}` +
        `&code_challenge_method=S256` +
        `&state=${encodeURIComponent(state)}`
    );
    await page.waitForLoadState('domcontentloaded');

    const escapedRedirectUri = options.accountsRedirectUri.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await clickAndWaitForUrl(
      page,
      '#auth-btn',
      new RegExp(escapedRedirectUri + '/?\\?code='),
      'accounts のパスキー認証'
    );

    const authorizationCode = new URL(page.url()).searchParams.get('code');
    expect(authorizationCode).toBeTruthy();

    // --- トークン交換（public client なので PKCE 必須） ---
    const tokenResponse = await api.post(`${options.baseUrl}/v1/token`, {
      form: {
        client_id: options.accountsClientId,
        code: authorizationCode!,
        redirect_uri: options.accountsRedirectUri,
        grant_type: 'authorization_code',
        scope: 'openid',
        code_verifier: codeVerifier,
      },
    });
    if (tokenResponse.status() !== 200) {
      throw new Error(`トークン交換に失敗しました (${tokenResponse.status()}): ${await tokenResponse.text()}`);
    }
    const accessToken = (await tokenResponse.json()).access_token as string;
    expect(accessToken).toBeTruthy();

    return { accessToken, messageId: message.ID };
  } finally {
    await page.close();
  }
}

/**
 * ボタンを押して期待する遷移を待つ。遷移しなかった場合は、Razor ページの #status に
 * 出ている失敗理由と現在の URL を添えて落とす（タイムアウトだけでは原因が分からないため）。
 */
async function clickAndWaitForUrl(
  page: Page,
  selector: string,
  url: RegExp,
  what: string,
  timeout = 20000
): Promise<void> {
  await page.click(selector);
  try {
    await page.waitForURL(url, { timeout });
  } catch (e) {
    const status = await page.locator('#status').textContent().catch(() => null);
    throw new Error(
      `${what} が完了しませんでした（現在の URL: ${page.url()}）。` +
        `status: ${status ?? '(表示なし)'} / 元エラー: ${(e as Error).message}`
    );
  }
}
