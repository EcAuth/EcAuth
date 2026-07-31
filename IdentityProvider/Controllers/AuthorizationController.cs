using IdentityProvider.Filters;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdpUtilities;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Controllers
{
    [ServiceFilter(typeof(OrganizationFilter))]
    [Route("v{version:apiVersion}/authorization")]
    [ApiController]
    [ApiVersion("1.0")]
    public class AuthorizationController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AuthorizationController> _logger;

        public AuthorizationController(
            EcAuthDbContext context,
            ITenantService tenantService,
            IConfiguration configuration,
            ILogger<AuthorizationController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        /// <summary>IdP の Authorization endpoint にリダイレクトします。</summary>
        /// <remarks>
        /// 必要なパラメータは以下のとおり
        /// - client.client_id
        /// - open_id_provider.name
        /// - redirect_uri
        /// - state（オプション：クライアントから渡された場合はコールバック時にそのまま返す）
        /// - code_challenge / code_challenge_method（オプション：PKCE (RFC 7636)。S256 のみ）
        /// パラメータで OpenID Provider を特定し、その IdP の Authorization endpoint にリダイレクトします。
        /// リダイレクト時に以下のパラメータを付与します。
        /// - client_id=<open_id_provider.client_id>
        /// - scope=<open_id_provider_scope.scope(スペース区切り)>
        /// - response_type=code
        /// - redirect_uri=<clientに登録したredirect_uri>
        /// - state=<state>
        /// </remarks>
        public async Task<IActionResult> Federate(
            [FromQuery] string client_id,
            [FromQuery] string provider_name,
            [FromQuery] string redirect_uri,
            [FromQuery] string? state,
            [FromQuery] string? code_challenge,
            [FromQuery] string? code_challenge_method)
        {
            _logger.LogDebug("Authorization request for tenant: {TenantName}", _tenantService.TenantName);

            // PKCE (RFC 7636) パラメータの検証。
            // エラーは redirect_uri へリダイレクトせず 400 で返す。redirect_uri の正当性を
            // 検証する前にリダイレクトするとオープンリダイレクタになるため。
            string? codeChallengeMethod = null;
            if (string.IsNullOrEmpty(code_challenge) && Security.PkcePolicy.IsRequired(_configuration))
            {
                // 必須化時は認可の入口で弾く。トークン交換時だけの拒否だと、外部 IdP での
                // 認証まで進んでから失敗するため原因が分かりにくい。
                _logger.LogWarning("PKCE required but code_challenge missing for client: {ClientId}", client_id);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "code_challenge が必要です。"
                });
            }
            if (!string.IsNullOrEmpty(code_challenge))
            {
                if (!Security.PkceValidator.IsValidChallengeFormat(code_challenge))
                {
                    _logger.LogWarning("Invalid code_challenge format for client: {ClientId}", client_id);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "code_challenge の形式が不正です。"
                    });
                }

                // 未指定時は S256 を既定とする（本 IdP は S256 のみサポート）
                codeChallengeMethod = string.IsNullOrEmpty(code_challenge_method) ? "S256" : code_challenge_method;
                if (codeChallengeMethod != "S256")
                {
                    _logger.LogWarning("Unsupported code_challenge_method: {Method}", code_challenge_method);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "code_challenge_method は S256 のみサポートします。"
                    });
                }
            }
            else if (!string.IsNullOrEmpty(code_challenge_method))
            {
                // method だけ指定されても PKCE は成立しない。黙って無視すると
                // クライアントは PKCE が効いていると誤認するため明示的に拒否する。
                _logger.LogWarning("code_challenge_method without code_challenge for client: {ClientId}", client_id);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "code_challenge_method には code_challenge が必要です。"
                });
            }

            var Client = await _context.Clients
                .Where(c => c.ClientId == client_id)
                .FirstOrDefaultAsync();
            if (Client == null)
            {
                _logger.LogWarning("Client not found: {ClientId}", client_id);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "client_id が不正です。"
                });
            }

            var OpenIdProvider = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .Where(
                    p => p.Name == provider_name
                    && p.ClientId == Client.Id
                ).FirstOrDefaultAsync();
            if (OpenIdProvider == null)
            {
                _logger.LogWarning("OpenID provider not found: {ProviderName} for client: {ClientId}", provider_name, client_id);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "provider_name が不正です。"
                });
            }

            var scopes = "openid email profile";
            if (OpenIdProvider.Name == "amazon-oauth2")
            {
                scopes = "profile postal_code profile:user_id";
            }
            var data = new State
            {
                OpenIdProviderId = OpenIdProvider.Id,
                RedirectUri = redirect_uri,
                ClientId = Client.Id,
                OrganizationId = Client.OrganizationId ?? 0,
                Scope = scopes,
                ClientState = state,  // クライアントの state を保存（RFC 6749 Section 4.1.2 準拠）
                // PKCE: 認可コード発行は外部 IdP からのコールバック時なので、封緘した State で往復させる
                CodeChallenge = code_challenge,
                CodeChallengeMethod = codeChallengeMethod
            };
            // STATE_PASSWORD は IConfiguration 経由で解決する（環境変数プロバイダも含むため
            // 既存のデプロイと互換）。seal 側と unseal 側（AuthorizationCallbackController /
            // TokenController）で解決経路を必ず揃えること。片方だけ変えると、環境変数以外で
            // 設定された場合に封緘した State を開封できなくなる。
            var password = _configuration["STATE_PASSWORD"];
            var options = new Iron.Options();

            var sealedData = await Iron.Seal<State>(data, password, options);
            // AuthorizationEndpoint に既存のクエリパラメータが含まれている場合は & で連結
            var separator = OpenIdProvider.AuthorizationEndpoint?.Contains("?") == true ? "&" : "?";
            return Redirect(
                $"{OpenIdProvider.AuthorizationEndpoint}" +
                $"{separator}client_id={OpenIdProvider.IdpClientId}" +
                $"&scope={Uri.EscapeDataString(scopes)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(_configuration["DEFAULT_ORGANIZATION_REDIRECT_URI"] ?? "https://localhost:8081/v1/auth/callback")}" +
                $"&state={Uri.EscapeDataString(sealedData)}"
             );
        }
    }
}
