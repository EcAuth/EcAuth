import { test, expect, APIRequestContext, BrowserContext, request } from '@playwright/test';
import { signupAndGetAccountToken, fetchSignupClient } from '../helpers/accounts';
import { createMailbox, Mailbox } from '../helpers/mailbox';

/**
 * 課金 API（EcAuthDocs#119 PR-1）: 支払い方法の登録状況・見込み額・Checkout / Portal の URL・Stripe Webhook の受け口を、
 * Stripe に接続しない Fake provider（Billing__Provider=Fake）で通す。
 *
 * ローカル / CI 専用。デプロイ済み環境（E2E_TENANT_BASE_DOMAIN あり）は本物の Stripe に繋がるか、
 * 課金機能が無効（404）なので skip する。Checkout / Portal の URL は Billing__ReturnBaseUrl__accounts
 * （compose.yaml / playwright.yml で https://localhost:8081）にそのまま戻る。
 *
 * 筋書き: 申込 → Account トークン → 見込み額（申込月の Client は初月無料）→ Checkout（Fake は即カード登録扱い）
 * → refresh=1 で登録済みに変わる → Portal → Webhook の冪等化（同じイベント ID の再送は processed=false）。
 */
test.describe.serial('課金 API（Fake provider）', () => {
  const remote = Boolean(process.env.E2E_TENANT_BASE_DOMAIN);
  test.skip(remote, 'Fake provider はローカル / CI のみ（デプロイ済み環境は本物の Stripe か 404）');

  const baseUrl = process.env.E2E_BASE_URL || 'https://localhost:8081';
  const accountsHost = process.env.E2E_ACCOUNTS_HOST || 'accounts.ec-auth.io';
  const accountsPageBaseUrl = process.env.E2E_ACCOUNTS_PAGE_URL || `https://${accountsHost}:8081`;
  const accountsClientId = process.env.ACCOUNTS_CLIENT_ID || 'ecauth-admin-console';
  const accountsClientSecret = process.env.ACCOUNTS_CLIENT_SECRET || undefined;
  const accountsRedirectUri = process.env.ACCOUNTS_REDIRECT_URI || 'https://localhost:8081/auth/callback';
  const returnBaseUrl = (process.env.Billing__ReturnBaseUrl__accounts || 'https://localhost:8081').replace(/\/+$/, '');

  const runSuffix = `${Date.now()}-${Math.floor(Math.random() * 1000)}`;
  const siteHost = `e2e-billing-${runSuffix}.test`;
  const productionSiteUrl = `https://${siteHost}:8081/`;
  const email = `e2e-billing-${runSuffix}@e2e.ec-auth.io`;
  const expectedOrgCode = siteHost.replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  // 対象月は JST の暦月（UsageMonth と同じ +09:00 固定）。
  const currentYearMonth = new Date(Date.now() + 9 * 60 * 60 * 1000).toISOString().slice(0, 7);

  let api: APIRequestContext;
  let mailbox: Mailbox;
  let context: BrowserContext;
  let accountAccessToken: string;
  let clientDbId: number;

  test.describe.configure({ retries: 0 });

  test.beforeAll(async ({ browser }) => {
    console.log(`[billing-e2e] org_code=${expectedOrgCode} email=${email} year_month=${currentYearMonth}`);
    api = await request.newContext({ ignoreHTTPSErrors: true, extraHTTPHeaders: { Host: accountsHost } });
    mailbox = await createMailbox();
    context = await browser.newContext({ ignoreHTTPSErrors: true });
    await context.credentials.install();
    await context.route(/\/mypage\//, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>mypage stub</body></html>' })
    );
  });

  test.afterAll(async () => {
    await Promise.allSettled([mailbox?.cleanup(email), api?.dispose(), context?.close()]);
    await mailbox?.dispose();
  });

  function getBilling(token: string | undefined, query = '') {
    return api.get(`${baseUrl}/v1/account/billing${query}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    });
  }

  function postBilling(path: 'checkout' | 'portal', token: string) {
    return api.post(`${baseUrl}/v1/account/billing/${path}`, { headers: { Authorization: `Bearer ${token}` } });
  }

  function postWebhook(body: unknown) {
    return api.post(`${baseUrl}/v1/billing/stripe/webhook`, {
      headers: { 'Content-Type': 'application/json', 'Stripe-Signature': 'fake' },
      data: body,
    });
  }

  test('申込 → 確認 → Account トークン取得', async () => {
    test.setTimeout(90000);

    const result = await signupAndGetAccountToken(api, mailbox, context, {
      baseUrl,
      accountsHost,
      accountsPageBaseUrl,
      accountsClientId,
      accountsClientSecret,
      accountsRedirectUri,
      email,
      organizationName: `E2E Billing Org ${runSuffix}`,
      productionSiteUrl,
      ecCubeVersion: '4',
    });
    accountAccessToken = result.accessToken;
    expect(accountAccessToken).toBeTruthy();

    const client = await fetchSignupClient(api, baseUrl, accountAccessToken, expectedOrgCode);
    clientDbId = client.id;
  });

  test('トークン無しは 401、year_month の不正値は 422', async () => {
    const unauthorized = await getBilling(undefined);
    expect(unauthorized.status()).toBe(401);
    expect((await unauthorized.json()).error).toBe('invalid_token');

    const malformed = await getBilling(accountAccessToken, '?year_month=2026-13');
    expect(malformed.status()).toBe(422);
    const body = await malformed.json();
    expect(body.error).toBe('invalid_request');
    expect(body.field).toBe('year_month');
  });

  test('登録前: 支払い方法なし、申込月の Client は初月無料（first_month）', async () => {
    const response = await getBilling(accountAccessToken);
    expect(response.status()).toBe(200);
    const body = await response.json();

    expect(body.payment_method_registered).toBe(false);
    expect(body.payment_method_registered_at).toBeNull();
    expect(body.has_stripe_customer).toBe(false);

    const estimate = body.estimate;
    expect(estimate.year_month).toBe(currentYearMonth);
    expect(estimate.subtotal_jpy).toBe(0);
    expect(estimate.discount_jpy).toBe(0);
    expect(estimate.total_jpy).toBe(0);
    expect(estimate.plan.exempt).toBe(false);

    const org = estimate.organizations.find((o: any) => o.code === expectedOrgCode);
    expect(org, 'managed organization is listed').toBeTruthy();
    expect(org.is_billable).toBe(true);
    const client = org.clients.find((c: any) => c.id === clientDbId);
    expect(client, 'client is listed').toBeTruthy();
    expect(client.subject_type).toBe('b2b');
    expect(client.free_tier_mau).toBe(5);
    expect(client.monthly_active_users).toBe(0);
    expect(client.is_billable).toBe(false);
    expect(client.exempt_reason).toBe('first_month');
    expect(client.amount_jpy).toBe(0);
    expect(client.custom_pricing).toBe(false);
  });

  test('Portal は Customer 未作成なら 409 no_customer', async () => {
    const response = await postBilling('portal', accountAccessToken);
    expect(response.status()).toBe(409);
    expect((await response.json()).error).toBe('no_customer');
  });

  test('Checkout は戻り先 URL を返し（Fake）、refresh=1 で登録済みに変わる', async () => {
    const checkout = await postBilling('checkout', accountAccessToken);
    expect(checkout.status()).toBe(200);
    expect((await checkout.json()).url).toBe(`${returnBaseUrl}/mypage/?billing=setup_complete`);

    // Webhook を経ずに読むとキャッシュのまま（未登録）。refresh=1 で Stripe（Fake）に問い合わせて同期する。
    const stale = await getBilling(accountAccessToken);
    expect((await stale.json()).payment_method_registered).toBe(false);

    const refreshed = await getBilling(accountAccessToken, '?refresh=1');
    expect(refreshed.status()).toBe(200);
    const body = await refreshed.json();
    expect(body.has_stripe_customer).toBe(true);
    expect(body.payment_method_registered).toBe(true);
    expect(typeof body.payment_method_registered_at).toBe('string');

    // 同期後は refresh なしでも登録済み（DB のキャッシュが更新されている）
    const cached = await getBilling(accountAccessToken);
    expect((await cached.json()).payment_method_registered).toBe(true);
  });

  test('Portal は Customer 作成後に戻り先 URL を返す', async () => {
    const response = await postBilling('portal', accountAccessToken);
    expect(response.status()).toBe(200);
    expect((await response.json()).url).toBe(`${returnBaseUrl}/mypage/`);
  });

  test('Webhook: 初回は processed=true、同じイベント ID の再送は processed=false、壊れたボディは 400', async () => {
    const event = {
      id: `evt_e2e_${runSuffix}`,
      type: 'setup_intent.succeeded',
      data: { object: { object: 'setup_intent', customer: 'cus_e2e_unknown' } },
    };

    const first = await postWebhook(event);
    expect(first.status()).toBe(200);
    expect(await first.json()).toEqual({ received: true, processed: true });

    // 冪等化は stripe_webhook_event の主キー（本番と同じ SQL Server）で行う
    const redelivered = await postWebhook(event);
    expect(redelivered.status()).toBe(200);
    expect(await redelivered.json()).toEqual({ received: true, processed: false });

    const broken = await api.post(`${baseUrl}/v1/billing/stripe/webhook`, {
      headers: { 'Content-Type': 'application/json', 'Stripe-Signature': 'fake' },
      data: 'not json',
    });
    expect(broken.status()).toBe(400);
    expect((await broken.json()).error).toBe('invalid_signature');
  });
});
