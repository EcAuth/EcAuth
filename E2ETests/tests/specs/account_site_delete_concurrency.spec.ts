import { test, expect, APIRequestContext, BrowserContext, request } from '@playwright/test';
import { signupAndGetAccountToken } from '../helpers/accounts';
import { createMailbox, Mailbox } from '../helpers/mailbox';

/**
 * サンドボックス追加と、その親（本番サイト）の削除が競合しても、
 * **論理削除済みの親を指す有効なサンドボックスが残らない**ことを検証する。
 *
 * ロックが無いと次のすれ違いが起きる:
 *   1. 追加リクエストがトランザクション外のスナップショットで親を「有効」と判定する
 *   2. 削除リクエストが親を論理削除する。このとき追加中のサンドボックスはまだコミットされて
 *      いないため、削除側のカスケード対象に入らない
 *   3. 追加がコミットされ、削除済みの親を指す有効なサンドボックスが残る
 *
 * 一度できてしまうと削除側が後から拾い直す経路は無く、`GET /v1/account/organizations` は
 * 親のいないサンドボックスを返し続ける。AccountController は追加・削除の両方で同じ
 * アカウント行を `UPDLOCK, HOLDLOCK` で取ることでこれを直列化している。
 *
 * ロックは実 SQL Server でしか効かない（InMemory はフォールバック）ため、この検証は E2E に置く。
 */
test.describe.serial('サンドボックス追加と親削除の競合', () => {
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
  const siteHost = `e2e-race-${runSuffix}.test`;
  const email = `e2e-race-${runSuffix}@e2e.ec-auth.io`;

  const accountsApiBaseUrl = remote ? `https://${accountsHost}` : baseUrl;

  // 競合はタイミング依存なので複数ラウンド試す。ロックが無い実装では、
  // このいずれかのラウンドで孤立サンドボックスが生まれる。
  const ROUNDS = 5;

  let api: APIRequestContext;
  let mailbox: Mailbox;
  let context: BrowserContext;
  let accessToken: string;

  test.describe.configure({ retries: 0 });

  const authHeaders = () => ({ Authorization: `Bearer ${accessToken}` });

  type OrganizationSummary = {
    id: number;
    is_sandbox: boolean;
    parent_organization_id: number | null;
  };

  const listOrganizations = async (): Promise<OrganizationSummary[]> => {
    const response = await api.get(`${accountsApiBaseUrl}/v1/account/organizations`, {
      headers: authHeaders(),
    });
    expect(response.status()).toBe(200);
    return (await response.json()).organizations;
  };

  test.beforeAll(async ({ browser }) => {
    console.log(`[delete-race] site=${siteHost} email=${email}`);

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
      organizationName: `E2E Delete Race ${runSuffix}`,
      productionSiteUrl: `https://${siteHost}/`,
      ecCubeVersion: '4',
    });
    accessToken = result.accessToken;
  });

  test.afterAll(async () => {
    await Promise.allSettled([mailbox?.cleanup(email), api?.dispose(), context?.close()]);
    await mailbox?.dispose();
  });

  test('サンドボックス追加と親削除を同時に投げても孤立サンドボックスが残らない', async () => {
    for (let round = 0; round < ROUNDS; round++) {
      // 各ラウンドで使い捨ての本番サイトを用意する（削除するため再利用できない）。
      const created = await api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
        headers: authHeaders(),
        data: { site_url: `https://e2e-race-${runSuffix}-p${round}.test/` },
      });
      expect(created.status(), `round ${round}: 本番サイトの用意`).toBe(201);
      const parentId = (await created.json()).id as number;

      // サンドボックス追加と親削除を同時に投げる。
      const [addResponse, deleteResponse] = await Promise.all([
        api.post(`${accountsApiBaseUrl}/v1/account/organizations`, {
          headers: authHeaders(),
          data: {
            site_url: `https://e2e-race-${runSuffix}-s${round}.test/`,
            is_sandbox: true,
            parent_organization_id: parentId,
          },
        }),
        api.post(`${accountsApiBaseUrl}/v1/account/organizations/${parentId}/delete`, {
          headers: authHeaders(),
        }),
      ]);

      // どちらが先に処理されるかは決まらない。許されるのは次の 2 通りだけ:
      //   - 追加が先: サンドボックスが作られ、削除がそれをカスケードで消す
      //   - 削除が先: 追加が invalid_parent で弾かれる
      const addStatus = addResponse.status();
      expect([201, 422], `round ${round}: 追加のステータス=${addStatus}`).toContain(addStatus);
      if (addStatus === 422) {
        expect((await addResponse.json()).error).toBe('invalid_parent');
      }
      expect(deleteResponse.status(), `round ${round}: 削除のステータス`).toBe(200);

      // 本題の不変条件: 一覧に出るサンドボックスは、必ず親も一覧に出ている
      //（＝親が有効である）こと。孤立サンドボックスはここで検出される。
      const organizations = await listOrganizations();
      const visibleIds = new Set(organizations.map((o) => o.id));
      const orphans = organizations.filter(
        (o) => o.is_sandbox && (o.parent_organization_id === null || !visibleIds.has(o.parent_organization_id))
      );

      expect(
        orphans,
        `round ${round}: 削除済みの親を指すサンドボックスが残った: ${JSON.stringify(orphans)}`
      ).toEqual([]);
    }
  });
});
