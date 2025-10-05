import { test, expect, request } from '@playwright/test';

test.describe.serial('認可コードフローフェデレーションのテストをします', () => {

  // 環境変数から動的にエンドポイントを取得（デフォルト値はGitHub Actions用）
  const authorizationEndpoint = process.env.E2E_AUTHORIZATION_ENDPOINT || 'https://localhost:8081/authorization';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || 'https://localhost:8081/token';
  const ecAuthUserInfoEndpoint = process.env.E2E_ECAUTH_USERINFO_ENDPOINT || 'https://localhost:8081/userinfo';
  const externalIdpUserInfoEndpoint = process.env.E2E_USERINFO_ENDPOINT || 'https://localhost:9091/userinfo';
  const redirectUri = process.env.E2E_REDIRECT_URI || 'https://localhost:8081/auth/callback';
  const clientId = 'client_id';
  const clientSecret = 'client_secret';
  const scopes = 'openid profile email';
  const providerName = 'federate-oauth2';
  const state = 'state';

  test.use({
    httpCredentials: {
      username: 'defaultuser@example.com',
      password: 'password',
    },
  });

  test('フェデレーションをテストをします', async ({ page }) => {
    const tokenRequest = await request.newContext();
    const authUrl = `${authorizationEndpoint}?client_id=${clientId}&redirect_uri=${encodeURIComponent(redirectUri)}&response_type=code&scope=${encodeURIComponent(scopes)}&provider_name=${providerName}&state=${state}`;
    console.log('🔵 Opening authorization URL:', authUrl);

    // ページナビゲーションのイベントをログ
    page.on('framenavigated', (frame) => {
      if (frame === page.mainFrame()) {
        console.log('📍 Navigated to:', frame.url());
      }
    });

    // コンソールログを表示
    page.on('console', (msg) => {
      console.log('🖥️ Browser console:', msg.type(), msg.text());
    });

    await page.goto(authUrl);
    console.log('📍 Initial navigation complete. Current URL:', page.url());

    // 外部IdPからのコールバック後、EcAuthの認可画面が表示されるのを待つ
    try {
      console.log('⏳ Waiting for authorization callback page...');
      await page.waitForURL(/\/auth\/callback/, { timeout: 10000 });
      console.log('✅ Authorization callback page loaded. URL:', page.url());

      // ページの内容を確認
      const pageTitle = await page.title();
      console.log('📄 Page title:', pageTitle);

      // 承認ボタンが存在するか確認
      const authorizeButton = await page.locator('button[value="authorize"]').count();
      console.log('🔘 Authorize button found:', authorizeButton > 0);

      if (authorizeButton > 0) {
        // 認可画面で「承認」ボタンをクリック
        console.log('👆 Clicking authorize button...');
        await page.click('button[value="authorize"]');
        console.log('✅ Authorize button clicked');
      } else {
        console.log('❌ Authorize button not found on page');
        const pageContent = await page.content();
        console.log('📄 Page content preview:', pageContent.substring(0, 500));
      }

      // クライアントへのリダイレクトを待つ
      console.log('⏳ Waiting for redirect to client with authorization code...');
      await page.waitForURL(new RegExp(redirectUri.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\?code='), { timeout: 10000 });
      console.log('✅ Redirected to client with code');
    } catch (error) {
      console.log('❌ Error during authorization flow:', error);
      console.log('📍 Current URL:', page.url());
      const pageContent = await page.content();
      console.log('📄 Current page content preview:', pageContent.substring(0, 500));
    }

    const url = new URL(page.url());
    console.log('🎯 Final URL:', url.toString());
    console.log('🔑 Authorization code:', url.searchParams.get('code'));
    console.log('🏷️ State:', url.searchParams.get('state'));

    // トークンエンドポイントへのリクエスト
    const tokenRequestData = {
      client_id: clientId,
      client_secret: clientSecret,
      code: url.searchParams.get('code') ?? '',
      scope: scopes,
      redirect_uri: redirectUri,
      grant_type: 'authorization_code',
      state: (url.searchParams.get('state') ?? '')
    };

    console.log('📤 Sending token request to:', tokenEndpoint);
    console.log('📋 Token request data:', JSON.stringify(tokenRequestData, null, 2));

    const response = await tokenRequest.post(tokenEndpoint, {
      form: tokenRequestData
    });

    console.log('📥 Token response status:', response.status());
    console.log('📥 Token response headers:', response.headers());

    const responseBody = await response.json();
    console.log('📥 Token response body:', JSON.stringify(responseBody, null, 2));

    if (responseBody.error) {
      console.log('❌ Token request failed with error:', responseBody.error);
      console.log('❌ Error description:', responseBody.error_description);
      if (responseBody.debug_info) {
        console.log('🐛 Debug info:');
        console.log('   Exception type:', responseBody.debug_info.exception_type);
        console.log('   Message:', responseBody.debug_info.message);
        console.log('   Stack trace:', responseBody.debug_info.stack_trace);
      }
    } else {
      console.log('✅ Token request successful');
    }

    expect(responseBody.access_token).toBeTruthy();
    expect(responseBody.token_type).toBe('Bearer');

    // UserInfo エンドポイントのテスト
    console.log('📤 Sending UserInfo request to:', ecAuthUserInfoEndpoint);
    console.log('🔑 Using access token:', responseBody.access_token.substring(0, 20) + '...');

    const userInfoRequest = await request.newContext();
    const userInfoResponse = await userInfoRequest.get(ecAuthUserInfoEndpoint, {
      headers: {
        Authorization: `Bearer ${responseBody.access_token}`
      }
    });

    console.log('📥 UserInfo response status:', userInfoResponse.status());
    console.log('📥 UserInfo response headers:', userInfoResponse.headers());

    const userInfoBody = await userInfoResponse.json();
    console.log('📥 UserInfo response body:', JSON.stringify(userInfoBody, null, 2));

    if (userInfoBody.error) {
      console.log('❌ UserInfo request failed with error:', userInfoBody.error);
      console.log('❌ Error description:', userInfoBody.error_description);
    } else {
      console.log('✅ UserInfo request successful');
    }

    expect(userInfoResponse.status()).toBe(200);
    expect(userInfoBody.sub).toBeTruthy();
    console.log('✅ UserInfo endpoint test completed successfully');
  });
});
