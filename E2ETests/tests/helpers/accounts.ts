import { expect, APIRequestContext, BrowserContext, Page } from '@playwright/test';
import { extractTokenFromMessage, Mailbox } from './mailbox';
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
  /**
   * 申込 API（/api/signup/*）とトークン交換の宛先。
   * ローカルでは IdP のベース URL（https://localhost:8081）＋ Host ヘッダでテナントを解決するが、
   * デプロイ済み環境では実ホスト（https://stg-accounts.ec-auth.io）をそのまま渡す。
   */
  baseUrl: string;
  /** accounts テナントに解決させる Host ヘッダ */
  accountsHost: string;
  /** accounts テナントのページ配信元（origin と rp_id を一致させる） */
  accountsPageBaseUrl: string;
  /** 管理コンソール Client */
  accountsClientId: string;
  accountsRedirectUri: string;
  /**
   * 管理コンソール Client が confidential の場合に渡す。
   * 本番の accounts は public client（PKCE のみ）だが、stg-accounts は
   * ACCOUNTS_CLIENT_PUBLIC 相当の設定を持たず confidential のままなので、
   * こちらを指定しないとトークン交換が「client_secretが正しくありません。」で落ちる。
   */
  accountsClientSecret?: string;

  email: string;
  organizationName: string;
  /** 申込するサイトの URL。ここから Organization code / rp_id / redirect_uri が導出される */
  productionSiteUrl: string;
  /** テストサイトの URL（任意）。渡すとサンドボックス Org が本番の子として一緒に作られる。 */
  testSiteUrl?: string;
  /** "2" | "4" | "other" */
  ecCubeVersion: string;
}

export interface SignupResult {
  /** SubjectType=Account のアクセストークン。/v1/account/* の認可に使う */
  accessToken: string;
}

/** 申込で払い出された Client。値はすべて API から取得したもので、テスト側では組み立てない。 */
export interface SignupClient {
  id: number;
  clientId: string;
  clientSecret: string;
  organizationCode: string;
  /** 申込時に登録された redirect_uri。プラグインが送る値と完全一致する必要がある */
  redirectUris: string[];
  /** 申込時に登録された allowed_rp_ids。ブラウザの origin と一致していなければ登録・認証が通らない */
  allowedRpIds: string[];
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
  mailbox: Mailbox,
  context: BrowserContext,
  options: SignupOptions
): Promise<SignupResult> {
  const { codeVerifier, codeChallenge } = generatePkcePair();

  // 認可コードは URL から読み取るだけで、コールバック先の実体は要らない。
  // デプロイ済み環境では redirect_uri が実在のホストを指すため、stub しないと
  // ブラウザがそこへ本当に遷移する。相手が SPA だと同じ code を別の code_verifier で
  // 交換しに行き、こちらのトークン交換が使えなくなる。
  await context.route(`${options.accountsRedirectUri}**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      body: '<html><body>auth callback stub</body></html>',
    })
  );

  // --- 申込 ---
  const requestResponse = await api.post(`${options.baseUrl}/api/signup/request`, {
    data: {
      email: options.email,
      organization_name: options.organizationName,
      contact_name: 'E2E Tester',
      production_site_url: options.productionSiteUrl,
      test_site_url: options.testSiteUrl,
      ec_cube_version: options.ecCubeVersion,
    },
  });
  if (requestResponse.status() !== 202) {
    throw new Error(`申込に失敗しました (${requestResponse.status()}): ${await requestResponse.text()}`);
  }

  // --- 確認メール → confirm ---
  const message = await mailbox.waitForMessage(options.email, { subjectIncludes: 'お申し込み確認' });
  const confirmToken = extractTokenFromMessage(message);

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

    // --- トークン交換（PKCE 必須。confidential なら client_secret も添える） ---
    const tokenResponse = await api.post(`${options.baseUrl}/v1/token`, {
      form: {
        client_id: options.accountsClientId,
        ...(options.accountsClientSecret ? { client_secret: options.accountsClientSecret } : {}),
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

    return { accessToken };
  } finally {
    await page.close();
  }
}

/**
 * マイページと同じ経路（Account トークン → 一覧 → secret の reveal）で、申込が払い出した
 * Client を取得する。DB を直接読まないのは、顧客が実際に client_id / client_secret を
 * 手に入れる経路そのものを検証するため。
 */
export async function fetchSignupClient(
  api: APIRequestContext,
  baseUrl: string,
  accessToken: string,
  organizationCode: string
): Promise<SignupClient> {
  const headers = { Authorization: `Bearer ${accessToken}` };

  const listResponse = await api.get(`${baseUrl}/v1/account/clients`, { headers });
  if (listResponse.status() !== 200) {
    throw new Error(
      `Client 一覧の取得に失敗しました (${listResponse.status()}): ${await listResponse.text()}`
    );
  }

  const clients = (await listResponse.json()).clients as Array<{
    id: number;
    client_id: string;
    organization_code: string;
    redirect_uris: string[];
    allowed_rp_ids: string[];
  }>;

  const client = clients.find((c) => c.organization_code === organizationCode);
  if (!client) {
    throw new Error(
      `organization_code=${organizationCode} の Client が見つかりません` +
        `（取得できたのは ${clients.map((c) => c.organization_code).join(', ') || '(なし)'}）`
    );
  }

  const revealResponse = await api.post(`${baseUrl}/v1/account/clients/${client.id}/secret/reveal`, {
    headers,
  });
  if (revealResponse.status() !== 200) {
    throw new Error(
      `client_secret の取得に失敗しました (${revealResponse.status()}): ${await revealResponse.text()}`
    );
  }

  return {
    id: client.id,
    clientId: client.client_id,
    clientSecret: (await revealResponse.json()).client_secret as string,
    organizationCode: client.organization_code,
    redirectUris: client.redirect_uris,
    allowedRpIds: client.allowed_rp_ids ?? [],
  };
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
