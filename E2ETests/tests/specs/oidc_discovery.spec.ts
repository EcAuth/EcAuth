import { test, expect, request } from '@playwright/test';

/**
 * OIDC Discovery / JWKS エンドポイントの動作確認。
 *
 * 以前は run-e2e-docker.sh が /.well-known/openid-configuration を 200|404 の
 * いずれでも合格とみなしており、未実装でもチェックが通ってしまっていた。
 * 実装後はこのテストで 200 を厳格に要求し、Provider Metadata と JWKS の構造を検証する。
 *
 * 設計判断（EcAuthDocs#83）:
 * - issuer は案A サブドメイン型（{scheme}://{host}）。
 * - authorization_endpoint / response_types_supported / code_challenge_methods_supported は
 *   意図的に省略しているため「含まれないこと」も明示的に検証する。
 */
test.describe('OIDC Discovery / JWKS エンドポイントのテストをします', () => {
  // ローカル Docker / GitHub Actions では IdP は https://localhost:8081 で待ち受ける。
  // issuer は Request の {scheme}://{host} 由来のため localhost:8081 はポートを含む。
  const issuer = process.env.E2E_ISSUER || 'https://localhost:8081';
  const discoveryEndpoint =
    process.env.E2E_DISCOVERY_ENDPOINT || `${issuer}/.well-known/openid-configuration`;

  test('Discovery が 200 を返し Provider Metadata を広告すること', async () => {
    const ctx = await request.newContext({ ignoreHTTPSErrors: true });

    const res = await ctx.get(discoveryEndpoint);
    expect(res.status()).toBe(200);

    const meta = await res.json();

    // issuer と各エンドポイント URL が issuer を基底にしていること
    expect(meta.issuer).toBe(issuer);
    expect(meta.token_endpoint).toBe(`${issuer}/v1/token`);
    expect(meta.userinfo_endpoint).toBe(`${issuer}/v1/userinfo`);
    expect(meta.jwks_uri).toBe(`${issuer}/.well-known/jwks.json`);

    // 実装実態に合わせた広告値
    expect(meta.grant_types_supported).toContain('authorization_code');
    expect(meta.token_endpoint_auth_methods_supported).toEqual(
      expect.arrayContaining(['client_secret_post', 'none'])
    );
    expect(meta.id_token_signing_alg_values_supported).toContain('RS256');
    expect(meta.subject_types_supported).toContain('public');
    expect(meta.scopes_supported).toEqual(
      expect.arrayContaining(['openid', 'email', 'profile'])
    );
    expect(meta.claims_supported).toContain('sub');

    // 意図的に省略しているフィールドが含まれないこと（設計判断の固定）
    expect(meta).not.toHaveProperty('authorization_endpoint');
    expect(meta).not.toHaveProperty('response_types_supported');
    expect(meta).not.toHaveProperty('code_challenge_methods_supported');

    await ctx.dispose();
  });

  test('jwks_uri が 200 を返し RS256 公開鍵を JWK で配信すること', async () => {
    const ctx = await request.newContext({ ignoreHTTPSErrors: true });

    // Discovery から jwks_uri を取得し、その URL を辿って検証する（リンク整合性も担保）
    const discovery = await ctx.get(discoveryEndpoint);
    expect(discovery.status()).toBe(200);
    const jwksUri = (await discovery.json()).jwks_uri as string;

    const res = await ctx.get(jwksUri);
    expect(res.status()).toBe(200);

    const jwks = await res.json();
    expect(Array.isArray(jwks.keys)).toBe(true);
    // 既定テナント（example）には active な RsaKeyPair がシードされているため 1 件以上返る
    expect(jwks.keys.length).toBeGreaterThanOrEqual(1);

    const key = jwks.keys[0];
    expect(key.kty).toBe('RSA');
    expect(key.use).toBe('sig');
    expect(key.alg).toBe('RS256');
    expect(typeof key.kid).toBe('string');
    expect(key.kid.length).toBeGreaterThan(0);
    expect(typeof key.n).toBe('string');
    expect(key.n.length).toBeGreaterThan(0);
    expect(typeof key.e).toBe('string');
    expect(key.e.length).toBeGreaterThan(0);

    await ctx.dispose();
  });
});
