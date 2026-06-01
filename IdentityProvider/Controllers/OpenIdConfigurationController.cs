using IdentityProvider.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// OpenID Connect Discovery (RFC 8414 / OpenID Connect Discovery 1.0) エンドポイント。
    /// host ルート直下の ".well-known/openid-configuration" を GET で公開する（/v1/ 配下にしない）。
    /// issuer は IIssuerResolver 経由で HttpContext から動的取得（案A サブドメイン型）し、
    /// 各エンドポイント URL は issuer を基底に組み立てる。
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    public class OpenIdConfigurationController : ControllerBase
    {
        private readonly IIssuerResolver _issuerResolver;

        public OpenIdConfigurationController(IIssuerResolver issuerResolver)
        {
            _issuerResolver = issuerResolver;
        }

        /// <summary>
        /// OpenID Provider Metadata を返す。
        /// </summary>
        [HttpGet(".well-known/openid-configuration")]
        public IActionResult Get()
        {
            var issuer = _issuerResolver.GetIssuer();

            // JSON のキー順・スネークケースを確実に保つため、プロパティ名は
            // リテラルのスネークケース文字列で構築する（C# プロパティ名の自動変換に依存しない）。
            //
            // 意図的に省略しているフィールド（仕様上 RECOMMENDED だが本 IdP の設計判断で出さない）:
            // - authorization_endpoint:
            //     /v1/authorization は標準の OAuth2 認可エンドポイントではなく、
            //     外部 IdP へリダイレクトする非標準のフェデレーション proxy のため公開しない。
            //     これに伴い response_types_supported も省略する。
            // - response_types_supported:
            //     上記 authorization_endpoint 非公開に伴い省略。
            // - code_challenge_methods_supported:
            //     PKCE 未実装のため省略。
            var metadata = new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["token_endpoint"] = $"{issuer}/v1/token",
                ["userinfo_endpoint"] = $"{issuer}/v1/userinfo",
                ["jwks_uri"] = $"{issuer}/.well-known/jwks.json",
                ["grant_types_supported"] = new[] { "authorization_code" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "none" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["subject_types_supported"] = new[] { "public" },
                ["scopes_supported"] = new[] { "openid", "email", "profile" },
                ["claims_supported"] = new[] { "sub" }
            };

            return new JsonResult(metadata);
        }
    }
}
