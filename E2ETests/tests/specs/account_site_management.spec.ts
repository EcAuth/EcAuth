import { test, expect, APIRequestContext, BrowserContext, request } from '@playwright/test';
import { signupAndGetAccountToken } from '../helpers/accounts';
import { createMailbox, Mailbox } from '../helpers/mailbox';

/**
 * マイページからのサイト（Organization）追加・削除を、実バックエンドに対して通す（EcAuth#482）。
 *
 * 申込では本番・テストの片方しか登録しないケースがあり、後からもう一方を足す手段が
 * 無かった。サイト = Organization は 1:1 なので「追加」は Organization の新規作成になる。
 * ユニットテストは InMemory プロバイダで動くため、以下は実 DB でしか確かめられない:
 *   - フィルター付きユニークインデックスによる「1 本番 1 テスト」の担保
 *   - 論理削除しても組織コードが解放されないこと（unique 制約は削除済み行にも効く）
 *   - 削除済みサイトの client_id が /platform/v1/client-resolve から引けなくなること
 *
 * 申込と同じく疑似サイトのホストには .test（RFC 6761 の予約 TLD）を使い、run ごとに
 * 一意化して残存データとの衝突を避ける。
 */
test.describe.serial('マイページからのサイト追加・削除', () => {
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

  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const siteHost = `e2e-site-${runSuffix}.test`;
  const addedSiteHost = `e2e-added-${runSuffix}.test`;
  const productionSiteUrl = `https://${siteHost}/`;
  const email = `e2e-site-${runSuffix}@e2e.ec-auth.io`;

  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;

  let api: APIRequestContext;
  let mailbox: Mailbox;
  let context: BrowserContext;
  let accessToken: string;

  // 申込 Org の組織コードは host から導出されるためリトライしても同じになり、
  // 2 回目は必ず organization_already_exists になる（本当の失敗理由が隠れる）。
  test.describe.configure({ retries: 0 });

  const authHeaders = () => ({ Authorization: `Bearer ${accessToken}` });

  test.beforeAll(async ({ browser }) => {
    console.log(`[site-management] site=${siteHost} added=${addedSiteHost} email=${email}`);

    api = await request.newContext({
      ignoreHTTPSErrors: true,
      ...(remote ? {} : { extraHTTPHeaders: { Host: accountsHost } }),
    });
    mailbox = await createMailbox();

    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();

    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );

    const result = await signupAndGetAccountToken(api, mailbox, context, {
      baseUrl: accountsApiBaseUrl,
      accountsHost,
      accountsPageBaseUrl,
      accountsClientId,
      accountsClientSecret,
      accountsRedirectUri,
      email,
      organizationName: `E2E Site Management ${runSuffix}`,
      productionSiteUrl,
      ecCubeVersion: '4',
    });
    accessToken = result.accessToken;
  });

  test.afterAll(async () => {
    await Promise.allSettled([mailbox?.cleanup(email), api?.dispose(), context?.close()]);
    await mailbox?.dispose();
  });

  let productionOrgId: number;
  let addedOrgId: number;
  let sandboxOrgId: number;
  let addedClientId: string;

  test('申込直後は本番サイト 1 件が一覧に出る', async () => {
    const response = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
    });
    expect(response.status()).toBe(200);

    const body = await response.json();
    expect(body.organizations).toHaveLength(1);
    expect(body.production_site_count).toBe(1);
    // 既定の上限。account.max_sites は DB 直更新で個別に変えられる。
    expect(body.max_sites).toBe(10);

    const [organization] = body.organizations;
    expect(organization.is_sandbox).toBe(false);
    expect(organization.parent_organization_id).toBeNull();
    expect(organization.clients).toHaveLength(1);
    productionOrgId = organization.id;
  });

  test('本番サイトを追加できる', async () => {
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: { site_url: `https://${addedSiteHost}/`, ec_cube_version: '4' },
    });
    expect(response.status()).toBe(201);

    const body = await response.json();
    expect(body.is_sandbox).toBe(false);
    expect(body.code).toBe(addedSiteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, ''));
    // 初期 redirect_uri は申込と同じ導出（EC-CUBE 4 系は /ecauth/callback）。
    expect(body.client.redirect_uris).toEqual([`https://${addedSiteHost}/ecauth/callback`]);
    expect(body.client.allowed_rp_ids).toContain(addedSiteHost);

    addedOrgId = body.id;
    addedClientId = body.client.client_id;
  });

  test('追加したサイトの client_id が client-resolve で解決できる', async () => {
    // プラグインはこのエンドポイントが返す base_url を保存して以降の API 呼び出しに使う。
    const response = await api.get(
      `${baseUrl}/platform/v1/client-resolve?client_id=${encodeURIComponent(addedClientId)}`
    );
    expect(response.status()).toBe(200);

    const body = await response.json();
    expect(body.tenant_name).toBe(addedSiteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, ''));
  });

  test('本番サイトと同じドメインでもテストサイトを追加できる', async () => {
    // 組織コードは -sandbox 接尾辞で分かれるため、自分の本番 Org とは衝突しない。
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: {
        site_url: `https://${addedSiteHost}/`,
        is_sandbox: true,
        parent_organization_id: addedOrgId,
      },
    });
    expect(response.status()).toBe(201);

    const body = await response.json();
    expect(body.is_sandbox).toBe(true);
    expect(body.code).toMatch(/-sandbox$/);
    expect(body.parent_organization_id).toBe(addedOrgId);
    sandboxOrgId = body.id;
  });

  test('1 本番あたりテストサイトは 1 件まで', async () => {
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: {
        site_url: `https://e2e-second-${runSuffix}.test/`,
        is_sandbox: true,
        parent_organization_id: addedOrgId,
      },
    });
    expect(response.status()).toBe(422);
    expect((await response.json()).error).toBe('sandbox_already_exists');
  });

  test('管理外の Organization を親には指定できない', async () => {
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: {
        site_url: `https://e2e-orphan-${runSuffix}.test/`,
        is_sandbox: true,
        // 申込・追加で払い出された Id とは無関係の値。存在しても管理外なら同じ扱い。
        parent_organization_id: 999999,
      },
    });
    expect(response.status()).toBe(422);
    expect((await response.json()).error).toBe('invalid_parent');
  });

  test('本番サイトを削除すると配下のテストサイトも消える', async () => {
    const response = await api.post(
      `${accountsApiBaseUrl}/v1/account/organizations/${addedOrgId}/delete`,
      { headers: authHeaders() }
    );
    expect(response.status()).toBe(200);

    const body = await response.json();
    expect(body.deleted_organization_ids).toEqual(expect.arrayContaining([addedOrgId, sandboxOrgId]));

    // 一覧からは消え、申込で作られた本番サイトだけが残る。
    const list = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, { headers: authHeaders() });
    const listBody = await list.json();
    expect(listBody.organizations.map((o: { id: number }) => o.id)).toEqual([productionOrgId]);
  });

  test('削除したサイトの client_id は client-resolve から引けなくなる', async () => {
    // プラグインが接続先 IdP を解決できなくなることが「削除したら止まる」の実効的な担保。
    const response = await api.get(
      `${baseUrl}/platform/v1/client-resolve?client_id=${encodeURIComponent(addedClientId)}`
    );
    expect(response.status()).toBe(404);
  });

  test('削除したドメインは再登録できない', async () => {
    // 論理削除では組織コードを解放しない（課金集計で別サイトの利用期間が混ざるため）。
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: { site_url: `https://${addedSiteHost}/` },
    });
    expect(response.status()).toBe(422);
    expect((await response.json()).error).toBe('organization_deleted');
  });

  test('削除で枠が空くので同じ本番に新しいテストサイトを作れる', async () => {
    // 「サイト追加 → 動作確認 → 旧サイト削除」で付け替えるための前提。
    const response = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: {
        site_url: `https://e2e-restg-${runSuffix}.test/`,
        is_sandbox: true,
        parent_organization_id: productionOrgId,
      },
    });
    expect(response.status()).toBe(201);
    expect((await response.json()).parent_organization_id).toBe(productionOrgId);
  });
});
