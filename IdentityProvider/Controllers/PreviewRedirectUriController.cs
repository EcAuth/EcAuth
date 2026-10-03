using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Asp.Versioning;
using IdentityProvider.Filters;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// ecauth-website の PR プレビュー（ブランチエイリアス）を、テナントの管理コンソール Client の
    /// <c>redirect_uri</c> に登録・削除する運用 API（EcAuthDocs#159）。PR のワークフローがデプロイ後に登録し、
    /// クローズ時に削除する。
    /// <para>
    /// <c>redirect_uri</c> の照合は完全一致のまま（<see cref="AuthorizationController"/>）で、パターン許可はしない。
    /// 代わりにこの API が受け付ける値を <c>https://&lt;1 ラベル&gt;.&lt;テナントのプレビューパターン&gt;/auth/callback</c>
    /// （<see cref="IPreviewOriginResolver"/>）に限る。固定の <c>redirect_uri</c>（Seeder が入れる本来の値）は
    /// パターンに一致しないので、この API からは消せない。
    /// </para>
    /// <para>
    /// 認証は HTTP Basic で、資格情報はテナント別の設定値 <c>PreviewRedirectApi:ClientId:{tenant}</c> /
    /// <c>PreviewRedirectApi:ClientSecret:{tenant}</c>（本番は Key Vault 参照）と照合する。DB の Client としては
    /// 作らない: 管理コンソール Client は「テナントの Organization にある <see cref="SubjectType.Account"/> の Client」
    /// で引かれており（<c>MagicLinkService.ResolveAccountClientAsync</c>）、同じ Organization に Client を足すと
    /// どちらが返るか不定になるため。資格情報が未設定のテナント（accounts 等）では 404 を返し、存在も明かさない。
    /// </para>
    /// <para>
    /// サーバー間呼び出し専用のため CORS は付けない。
    /// </para>
    /// </summary>
    [Route("v{version:apiVersion}/preview/redirect-uris")]
    [ApiController]
    [ApiVersion("1.0")]
    [NoStore]
    public class PreviewRedirectUriController : ControllerBase
    {
        /// <summary>プレビューのフロントが認可コードを受け取るパス（ecauth-website の <c>authRedirectUri</c>）。</summary>
        public const string CallbackPath = "/auth/callback";

        /// <summary>
        /// 1 Client に登録できるプレビュー <c>redirect_uri</c> の上限（クローズ時の削除漏れで無制限に増えないように）。
        /// <para>
        /// 件数確認から挿入までは直列化していないので、並行した登録で 1〜2 件超えうる。目安の歯止めであって
        /// セキュリティ上の境界ではないため許容する。同じ URI の並行登録でできる重複行も無害（照合は存在判定
        /// （<see cref="AuthorizationController"/> / <c>B2BPasskeyController</c>）、削除は一致する行をすべて消す）。
        /// 一意制約で防ぐには <c>uri</c>（<c>nvarchar(max)</c>）のハッシュ列と 2 段のリリースが要り、見合わない。
        /// </para>
        /// </summary>
        public const int MaxPreviewRedirectUris = 50;

        private const string ConfigSection = "PreviewRedirectApi";

        // 環境変数名はハイフンを含められないため、キーのテナント部は [A-Za-z0-9_] 以外を "_" にする（他のテナント別キーと同じ）。
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IPreviewOriginResolver _previewOrigins;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PreviewRedirectUriController> _logger;

        public PreviewRedirectUriController(
            EcAuthDbContext context,
            ITenantService tenantService,
            IPreviewOriginResolver previewOrigins,
            IConfiguration configuration,
            ILogger<PreviewRedirectUriController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _previewOrigins = previewOrigins;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// PUT /v1/preview/redirect-uris?uri=https://&lt;branch&gt;.ecauth-website-stg.pages.dev/auth/callback
        /// 登録する。既に登録済みなら何もしない（PR の push ごとに呼ばれるため冪等）。
        /// </summary>
        [HttpPut("")]
        public async Task<IActionResult> Register([FromQuery] string? uri, CancellationToken ct)
        {
            var failure = Authenticate() ?? ValidateUri(uri);
            if (failure != null)
            {
                return failure;
            }

            var client = await ResolveAdminConsoleClientAsync(ct);
            if (client == null)
            {
                return NotFound();
            }

            var existing = await _context.RedirectUris
                .Where(r => r.ClientId == client.Id)
                .Select(r => r.Uri)
                .ToListAsync(ct);
            if (existing.Contains(uri!, StringComparer.Ordinal))
            {
                return Ok(new { redirect_uri = uri, created = false });
            }

            if (existing.Count(IsPreviewRedirectUri) >= MaxPreviewRedirectUris)
            {
                _logger.LogWarning(
                    "プレビューの redirect_uri が上限に達しています: Tenant={Tenant}, ClientId={ClientId}, Max={Max}",
                    _tenantService.TenantName, client.ClientId, MaxPreviewRedirectUris);
                return Conflict(new
                {
                    error = "too_many_preview_redirect_uris",
                    error_description = $"プレビューの redirect_uri は {MaxPreviewRedirectUris} 件までです。クローズ済み PR の分を削除してください。"
                });
            }

            var now = DateTimeOffset.UtcNow;
            _context.RedirectUris.Add(new RedirectUri
            {
                ClientId = client.Id,
                Uri = uri!,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "プレビューの redirect_uri を登録しました: Tenant={Tenant}, ClientId={ClientId}, Uri={Uri}",
                _tenantService.TenantName, client.ClientId, uri);
            return StatusCode(StatusCodes.Status201Created, new { redirect_uri = uri, created = true });
        }

        /// <summary>
        /// DELETE /v1/preview/redirect-uris?uri=...
        /// 削除する。未登録でも 204（PR クローズ時の後片付けは再実行されうるため冪等）。
        /// </summary>
        [HttpDelete("")]
        public async Task<IActionResult> Unregister([FromQuery] string? uri, CancellationToken ct)
        {
            var failure = Authenticate() ?? ValidateUri(uri);
            if (failure != null)
            {
                return failure;
            }

            var client = await ResolveAdminConsoleClientAsync(ct);
            if (client == null)
            {
                return NotFound();
            }

            var rows = await _context.RedirectUris
                .Where(r => r.ClientId == client.Id && r.Uri == uri)
                .ToListAsync(ct);
            if (rows.Count > 0)
            {
                _context.RedirectUris.RemoveRange(rows);
                await _context.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "プレビューの redirect_uri を削除しました: Tenant={Tenant}, ClientId={ClientId}, Uri={Uri}",
                    _tenantService.TenantName, client.ClientId, uri);
            }
            return NoContent();
        }

        /// <summary>
        /// Basic 認証。資格情報が未設定のテナントは 404（機能なし）、不一致は 401。
        /// 比較は SHA-256 に揃えてから定数時間で行う（長さの違いも漏らさない）。
        /// </summary>
        private IActionResult? Authenticate()
        {
            var tenantKey = NonConfigKeyChar.Replace(_tenantService.TenantName ?? string.Empty, "_");
            var expectedId = _configuration[$"{ConfigSection}:ClientId:{tenantKey}"];
            var expectedSecret = _configuration[$"{ConfigSection}:ClientSecret:{tenantKey}"];
            if (string.IsNullOrEmpty(expectedId) || string.IsNullOrEmpty(expectedSecret))
            {
                return NotFound();
            }

            if (!TryReadBasicCredentials(out var clientId, out var clientSecret)
                || !(FixedTimeEquals(clientId, expectedId) & FixedTimeEquals(clientSecret, expectedSecret)))
            {
                _logger.LogWarning(
                    "プレビュー redirect_uri API の認証に失敗しました: Tenant={Tenant}", _tenantService.TenantName);
                Response.Headers.WWWAuthenticate = "Basic";
                return Unauthorized(new
                {
                    error = "invalid_client",
                    error_description = "クライアント認証に失敗しました。"
                });
            }
            return null;
        }

        private bool TryReadBasicCredentials(out string clientId, out string clientSecret)
        {
            clientId = string.Empty;
            clientSecret = string.Empty;

            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header)
                || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(header.Parameter))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            }
            catch (FormatException)
            {
                return false;
            }

            var separator = decoded.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }
            clientId = decoded[..separator];
            clientSecret = decoded[(separator + 1)..];
            return true;
        }

        private static bool FixedTimeEquals(string actual, string expected) =>
            CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(actual)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

        private IActionResult? ValidateUri(string? uri)
        {
            if (IsPreviewRedirectUri(uri))
            {
                return null;
            }
            return BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "このテナントのプレビューのオリジンに /auth/callback を付けた URI を指定してください。"
            });
        }

        /// <summary><c>{プレビューのオリジン}/auth/callback</c>（クエリ・フラグメントなし）か。</summary>
        private bool IsPreviewRedirectUri(string? uri) =>
            uri != null
            && uri.EndsWith(CallbackPath, StringComparison.Ordinal)
            && _previewOrigins.IsAllowed(_tenantService.TenantName, uri[..^CallbackPath.Length]);

        /// <summary>
        /// テナントの管理コンソール Client（Organization にある <see cref="SubjectType.Account"/> の Client）。
        /// <c>MagicLinkService.ResolveAccountClientAsync</c> と同じ引き方（Seeder が投入する Client）。
        /// </summary>
        private Task<Client?> ResolveAdminConsoleClientAsync(CancellationToken ct)
        {
            var tenantName = _tenantService.TenantName;
            return _context.Clients
                .ExcludeDeletedOrganizations()
                .FirstOrDefaultAsync(
                    c => c.Organization != null
                        && c.Organization.TenantName == tenantName
                        && c.SubjectType == SubjectType.Account,
                    ct);
        }
    }
}
