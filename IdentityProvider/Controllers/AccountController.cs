using System.Net.Http.Headers;
using System.Security.Cryptography;
using Asp.Versioning;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdpUtilities.Security;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// マイページ（ec-auth.io）向けの Account API。
    /// AccessToken[SubjectType=Account] を必須とし、Account が管理する Organization の
    /// Client 情報（client_id / client_secret）の参照・secret 再生成を提供する。
    ///
    /// このエンドポイントは accounts / stg-accounts テナントでのみ機能する（Account トークンは
    /// これらのテナントのコンソール Client からのみ発行されるため、SubjectType.Account の
    /// 検証がテナント限定を担保する）。CORS は SignupApiCors（ec-auth.io / www）を流用する。
    /// </summary>
    [Route("v{version:apiVersion}/account")]
    [ApiController]
    [ApiVersion("1.0")]
    [EnableCors(SignupController.CorsPolicy)]
    public class AccountController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly ITokenService _tokenService;
        private readonly IAccountService _accountService;
        private readonly ISecretProtector _secretProtector;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            EcAuthDbContext context,
            ITokenService tokenService,
            IAccountService accountService,
            ISecretProtector secretProtector,
            ILogger<AccountController> logger)
        {
            _context = context;
            _tokenService = tokenService;
            _accountService = accountService;
            _secretProtector = secretProtector;
            _logger = logger;
        }

        /// <summary>
        /// GET /v1/account/clients
        /// 呼び出し Account が管理する Organization に属する Client 一覧を返す。
        /// client_secret は復号した平文を返し、マスク表示はマイページ（UI）側で行う。
        /// </summary>
        [HttpGet("clients")]
        public async Task<IActionResult> GetClients()
        {
            var subject = await ValidateAccountTokenAsync();
            if (subject == null)
            {
                return Unauthorized(new
                {
                    error = "invalid_token",
                    error_description = "有効な Account アクセストークンが必要です。"
                });
            }

            var managed = await _accountService.GetManagedOrganizationsAsync(subject);
            var orgIds = managed.Select(m => m.OrganizationId).ToHashSet();

            // 管理対象が無ければ Client も無いので、DB を引かず空一覧を返す。
            if (orgIds.Count == 0)
            {
                return Ok(new { clients = Array.Empty<object>() });
            }

            // 管理対象 Organization は顧客テナント（別テナント）のため IgnoreQueryFilters で横断取得する。
            var clients = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.Organization)
                .Include(c => c.RedirectUris)
                .Where(c => c.OrganizationId != null && orgIds.Contains(c.OrganizationId.Value))
                .ToListAsync();

            var result = new List<object>(clients.Count);
            foreach (var c in clients)
            {
                var revealedSecret = string.IsNullOrEmpty(c.ClientSecret)
                    ? string.Empty
                    : await _secretProtector.UnprotectAsync(c.ClientSecret);

                result.Add(new
                {
                    id = c.Id,
                    client_id = c.ClientId,
                    client_secret = revealedSecret,
                    app_name = c.AppName,
                    is_sandbox = c.Organization?.IsSandbox ?? false,
                    organization_code = c.Organization?.Code,
                    organization_name = c.Organization?.Name,
                    redirect_uris = c.RedirectUris.Select(r => r.Uri).ToArray()
                });
            }

            return Ok(new { clients = result });
        }

        /// <summary>
        /// POST /v1/account/clients/{id}/secret
        /// 指定 Client の client_secret を再生成する。呼び出し Account が管理する
        /// Organization に属する Client のみ許可する。生成した平文を1回だけ返す。
        /// </summary>
        [HttpPost("clients/{id:int}/secret")]
        public async Task<IActionResult> RegenerateSecret(int id)
        {
            var subject = await ValidateAccountTokenAsync();
            if (subject == null)
            {
                return Unauthorized(new
                {
                    error = "invalid_token",
                    error_description = "有効な Account アクセストークンが必要です。"
                });
            }

            var managed = await _accountService.GetManagedOrganizationsAsync(subject);
            var orgIds = managed.Select(m => m.OrganizationId).ToHashSet();

            // 管理対象が無ければ所有権チェックは必ず失敗するため、DB を引かず 404 を返す。
            if (orgIds.Count == 0)
            {
                _logger.LogWarning("Account {Subject} attempted to rotate secret for client {ClientDbId} without any managed organizations", subject, id);
                return NotFound(new
                {
                    error = "not_found",
                    error_description = "対象の Client が見つかりません。"
                });
            }

            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == id);

            // 所有権チェック: 対象 Client の Organization が呼び出し Account の管理対象か
            if (client == null || client.OrganizationId == null || !orgIds.Contains(client.OrganizationId.Value))
            {
                _logger.LogWarning("Account {Subject} attempted to rotate secret for client {ClientDbId} without ownership", subject, id);
                return NotFound(new
                {
                    error = "not_found",
                    error_description = "対象の Client が見つかりません。"
                });
            }

            var newSecret = GenerateClientSecret();
            client.ClientSecret = await _secretProtector.ProtectAsync(newSecret);
            client.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("client_secret regenerated for client {ClientId} by account {Subject}", client.ClientId, subject);

            return Ok(new
            {
                id = client.Id,
                client_id = client.ClientId,
                client_secret = newSecret
            });
        }

        /// <summary>
        /// Authorization: Bearer を検証し、SubjectType=Account のトークンのみ受理する。
        /// 有効な場合は subject を返し、そうでなければ null を返す。
        /// </summary>
        private async Task<string?> ValidateAccountTokenAsync()
        {
            var authorizationHeader = HttpContext.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                return null;
            }

            AuthenticationHeaderValue authHeaderValue;
            try
            {
                authHeaderValue = AuthenticationHeaderValue.Parse(authorizationHeader);
            }
            catch (FormatException)
            {
                return null;
            }

            // RFC 7235: auth-scheme は大文字小文字を区別しない
            if (!string.Equals(authHeaderValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(authHeaderValue.Parameter))
            {
                return null;
            }

            var validation = await _tokenService.ValidateAccessTokenWithTypeAsync(authHeaderValue.Parameter);
            if (!validation.IsValid || validation.SubjectType != SubjectType.Account || string.IsNullOrEmpty(validation.Subject))
            {
                return null;
            }

            return validation.Subject;
        }

        /// <summary>
        /// SignupService と同一形式（32バイトのランダム値を Base64URL）で client_secret を生成する。
        /// </summary>
        private static string GenerateClientSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64UrlTextEncoder.Encode(bytes);
        }
    }
}
