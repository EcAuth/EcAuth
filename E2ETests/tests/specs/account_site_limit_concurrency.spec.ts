import { test, expect, APIRequestContext, BrowserContext, request } from '@playwright/test';
import { signupAndGetAccountToken } from '../helpers/accounts';
import { createMailbox, Mailbox } from '../helpers/mailbox';

/**
 * 本番サイト数の上限が、同一アカウントの並行リクエストでも破られないことを検証する。
 *
 * 「アカウントあたりの本番サイト数」は集計値で DB 制約として表現できない。既存の制約は
 * どちらもこのケースを止められない:
 *   - IX_organization_parent_organization_id_active は parent_organization_id が非 null の
 *     行だけが対象で、本番 Org（null）は含まれない
 *   - organization.Code のユニーク制約は、別ドメイン同士なら衝突しない
 * したがって AccountController がトランザクション内で account 行に UPDLOCK/HOLDLOCK を掛けて
 * 直列化するのが唯一の保護になる。
 *
 * この spec が E2E にあるのは、ロックが**実 SQL Server でしか効かない**ため。
 * ユニットテストの InMemory プロバイダは生 SQL を実行できず、
 * LoadMaxSitesForUpdateAsync は通常の読み取りにフォールバックする。
 */
test.describe.serial('本番サイト上限の並行リクエスト耐性', () => {
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
  const siteHost = `e2e-limit-${runSuffix}.test`;
  const email = `e2e-limit-${runSuffix}@e2e.ec-auth.io`;

  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;

  // account.max_sites の既定値。DB を直接更新すれば変えられるが、E2E からは触らない。
  const MAX_SITES = 10;
  // 同時に投げるリクエスト数。上限ぴったりの状態から複数を並行させ、1 件も通らないことを見る。
  const CONCURRENCY = 4;

  let api: APIRequestContext;
  let mailbox: Mailbox;
  let context: BrowserContext;
  let accessToken: string;

  test.describe.configure({ retries: 0 });

  const authHeaders = () => ({ Authorization: `Bearer ${accessToken}` });

  const addProductionSite = (host: string) =>
    api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
      data: { site_url: `https://${host}/` },
    });

  test.beforeAll(async ({ browser }) => {
    console.log(`[site-limit] site=${siteHost} email=${email}`);

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
      organizationName: `E2E Site Limit ${runSuffix}`,
      productionSiteUrl: `https://${siteHost}/`,
      ecCubeVersion: '4',
    });
    accessToken = result.accessToken;
  });

  test.afterAll(async () => {
    await Promise.allSettled([mailbox?.cleanup(email), api?.dispose(), context?.close()]);
    await mailbox?.dispose();
  });

  test('上限ちょうどまで本番サイトを追加できる', async () => {
    // 申込で 1 件できているので、残りを順番に埋める。
    for (let i = 2; i <= MAX_SITES; i++) {
      const response = await addProductionSite(`e2e-limit-${runSuffix}-${i}.test`);
      expect(response.status(), `${i} 件目の追加`).toBe(201);
    }

    const list = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, { headers: authHeaders() });
    const body = await list.json();
    expect(body.production_site_count).toBe(MAX_SITES);
    expect(body.max_sites).toBe(MAX_SITES);
  });

  test('上限到達後は並行リクエストでも 1 件も作られない', async () => {
    // 別ドメインを同時に投げる。組織コードが互いに違うため unique 制約では止まらず、
    // アカウント行のロックだけが上限を守る。
    const responses = await Promise.all(
      Array.from({ length: CONCURRENCY }, (_, i) =>
        addProductionSite(`e2e-limit-${runSuffix}-race-${i}.test`)
      )
    );

    const statuses = responses.map((r) => r.status());
    expect(statuses.every((s) => s === 422), `statuses=${statuses.join(',')}`).toBe(true);

    for (const response of responses) {
      expect((await response.json()).error).toBe('site_limit_exceeded');
    }

    // 実際に増えていないことを DB 由来の一覧で確認する。
    const list = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, { headers: authHeaders() });
    expect((await list.json()).production_site_count).toBe(MAX_SITES);
  });

  test('1 枠空けると、並行リクエストのうち 1 件だけが成功する', async () => {
    // ここが本題。ロックが無いと全リクエストが同じカウントを読み、複数が 201 になる。
    const list = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, { headers: authHeaders() });
    const organizations = (await list.json()).organizations as Array<{ id: number; is_sandbox: boolean }>;
    const victim = organizations.filter((o) => !o.is_sandbox).at(-1)!;

    const deleted = await api.post(
      `${accountsApiBaseUrl}/v1/account/organizations/${victim.id}/delete`,
      { headers: authHeaders() }
    );
    expect(deleted.status()).toBe(200);

    const responses = await Promise.all(
      Array.from({ length: CONCURRENCY }, (_, i) =>
        addProductionSite(`e2e-limit-${runSuffix}-slot-${i}.test`)
      )
    );

    const statuses = responses.map((r) => r.status());
    const created = statuses.filter((s) => s === 201).length;
    const rejected = statuses.filter((s) => s === 422).length;

    expect(created, `statuses=${statuses.join(',')}`).toBe(1);
    expect(rejected).toBe(CONCURRENCY - 1);

    const after = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, { headers: authHeaders() });
    expect((await after.json()).production_site_count).toBe(MAX_SITES);
  });
});
