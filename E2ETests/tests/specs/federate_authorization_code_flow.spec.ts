import { test, expect, request } from '@playwright/test';

test.describe.serial('認可コードフローフェデレーションのテストをします', () => {

  // 環境変数から動的にエンドポイントを取得（デフォルト値はGitHub Actions用）
  const authorizationEndpoint = process.env.E2E_AUTHORIZATION_ENDPOINT || 'https://localhost:8081/v1/authorization';
  const tokenEndpoint = process.env.E2E_TOKEN_ENDPOINT || 'https://localhost:8081/v1/token';
  const ecAuthUserInfoEndpoint = process.env.E2E_ECAUTH_USERINFO_ENDPOINT || 'https://localhost:8081/v1/userinfo';
  const externalIdpUserInfoEndpoint = process.env.E2E_USERINFO_ENDPOINT || 'https://localhost:9091/userinfo';
  const redirectUri = process.env.E2E_REDIRECT_URI || 'https://localhost:8081/v1/auth/callback';
  const clientId = 'client_id';
  const clientSecret = 'client_secret';
  const scopes = 'openid profile email';
  const providerName = 'federate-oauth2';
  const state = 'state';

  // MockIdPのBasic認証情報
  const mockIdpCredentials = {
    username: 'defaultuser@example.com',
    password: 'password',
  };

  test('フェデレーションをテストをします', async ({ browser }) => {
    // MockIdPドメインへの認証情報を含むコンテキストを作成
    const mockIdpBaseUrl = process.env.MOCK_IDP_BASE_URL || 'https://mock-openid-provider.mangoplant-f8a75293.japaneast.azurecontainerapps.io';
    const mockIdpOrigin = new URL(mockIdpBaseUrl).origin;

    const context = await browser.newContext({
      ignoreHTTPSErrors: true,
      httpCredentials: {
        ...mockIdpCredentials,
        origin: mockIdpOrigin,
      },
    });
    const page = await context.newPage();
    const tokenRequest = await request.newContext();
    const authUrl = `${authorizationEndpoint}?client_id=${clientId}&redirect_uri=${encodeURIComponent(redirectUri)}&response_type=code&scope=${encodeURIComponent(scopes)}&provider_name=${providerName}&state=${state}`;

    console.log('========================================');
    console.log('🔵 E2E Test Configuration:');
    console.log('   Authorization Endpoint:', authorizationEndpoint);
    console.log('   Token Endpoint:', tokenEndpoint);
    console.log('   EcAuth UserInfo Endpoint:', ecAuthUserInfoEndpoint);
    console.log('   External IdP UserInfo Endpoint:', externalIdpUserInfoEndpoint);
    console.log('   Redirect URI:', redirectUri);
    console.log('   Provider Name:', providerName);
    console.log('   MockIdP Origin:', mockIdpOrigin);
    console.log('========================================');
    console.log('🔵 Opening authorization URL:', authUrl);

    // ネットワークリクエストのログ
    page.on('request', (request) => {
      console.log('🌐 Request:', request.method(), request.url());
    });

    // ネットワークレスポンスのログ
    page.on('response', (response) => {
      console.log('📨 Response:', response.status(), response.url());
    });

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

    // リクエスト失敗のログ
    page.on('requestfailed', (request) => {
      console.log('❌ Request failed:', request.url(), request.failure()?.errorText);
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

    // RFC 6749 Section 4.1.2: クライアントから送信した state がそのまま返されることを検証
    expect(url.searchParams.get('state')).toBe(state);

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

    // External UserInfo エンドポイントのテスト
    const externalUserInfoEndpoint = `${authorizationEndpoint.replace('/v1/authorization', '')}/v1/api/external-userinfo`;
    console.log('📤 Sending External UserInfo request to:', externalUserInfoEndpoint);
    console.log('🔑 Using access token:', responseBody.access_token.substring(0, 20) + '...');
    console.log('🏷️ Provider:', providerName);

    const externalUserInfoRequest = await request.newContext();
    const externalUserInfoResponse = await externalUserInfoRequest.get(
      `${externalUserInfoEndpoint}?provider=${providerName}`,
      {
        headers: {
          Authorization: `Bearer ${responseBody.access_token}`
        }
      }
    );

    console.log('📥 External UserInfo response status:', externalUserInfoResponse.status());
    console.log('📥 External UserInfo response headers:', externalUserInfoResponse.headers());

    const externalUserInfoBody = await externalUserInfoResponse.json();
    console.log('📥 External UserInfo response body:', JSON.stringify(externalUserInfoBody, null, 2));

    if (externalUserInfoBody.error) {
      console.log('❌ External UserInfo request failed with error:', externalUserInfoBody.error);
      console.log('❌ Error description:', externalUserInfoBody.error_description);
    } else {
      console.log('✅ External UserInfo request successful');
    }

    // External UserInfo レスポンスの検証
    // Note: 現在のMockIdPのUserInfoエンドポイントはsubのみを返します
    // email等の追加クレームはMockIdPの改修後に検証を追加
    expect(externalUserInfoResponse.status()).toBe(200);
    expect(externalUserInfoBody.sub).toBeTruthy();
    expect(externalUserInfoBody.provider).toBe(providerName);
    console.log('✅ External UserInfo endpoint test completed successfully');
    console.log('📊 External UserInfo claims:', {
      sub: externalUserInfoBody.sub,
      email: externalUserInfoBody.email ?? '(not provided by MockIdP)',
      name: externalUserInfoBody.name ?? '(not provided by MockIdP)',
      provider: externalUserInfoBody.provider
    });

    // コンテキストをクローズ
    await context.close();
  });
});
