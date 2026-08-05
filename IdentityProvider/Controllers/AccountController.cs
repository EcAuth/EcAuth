using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using IdentityProvider.Exceptions;
using IdentityProvider.Filters;
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
    // client_secret を返す経路があるため、レスポンスをキャッシュさせない。
    [NoStore]
    public class AccountController : ControllerBase
    {
        /// <summary>redirect_uri の登録上限。EC-CUBE 1 サイトあたり数件で足りる想定の安全弁。</summary>
        private const int MaxRedirectUris = 20;

        /// <summary>redirect_uri 1 件の最大長。ブラウザ・プロキシが安全に扱える範囲に合わせる。</summary>
        private const int MaxRedirectUriLength = 2048;

        /// <summary>RP ID の登録上限。</summary>
        private const int MaxAllowedRpIds = 20;

        /// <summary>RP ID 1 件の最大長（RFC 1035 のドメイン名上限）。</summary>
        private const int MaxRpIdLength = 253;

        /// <summary>
        /// <c>Client.AllowedRpIdsJson</c> の <c>[MaxLength(2000)]</c> に合わせたシリアライズ後の上限。
        /// </summary>
        private const int MaxAllowedRpIdsJsonLength = 2000;

        private readonly EcAuthDbContext _context;
        private readonly ITokenService _tokenService;
        private readonly IAccountService _accountService;
        private readonly ISecretProtector _secretProtector;
        private readonly IOrganizationProvisioningService _provisioning;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            EcAuthDbContext context,
            ITokenService tokenService,
            IAccountService accountService,
            ISecretProtector secretProtector,
            IOrganizationProvisioningService provisioning,
            ILogger<AccountController> logger)
        {
            _context = context;
            _tokenService = tokenService;
            _accountService = accountService;
            _secretProtector = secretProtector;
            _provisioning = provisioning;
            _logger = logger;
        }

        /// <summary>
        /// GET /v1/account/clients
        /// 呼び出し Account が管理する Organization に属する Client 一覧を返す。
        ///
        /// client_secret は **含めない**。一覧に全 Client の平文 secret を載せると、
        /// マイページに XSS が 1 箇所でも入った時点で管理下の全 Client の secret が
        /// 一度に流出する（単一障害点になる）ため、参照はユーザーの明示操作を伴う
        /// <see cref="RevealSecret"/> で 1 件ずつ行う。
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
                result.Add(new
                {
                    id = c.Id,
                    client_id = c.ClientId,
                    // 値そのものは返さず、設定済みかどうかだけを返す（UI のマスク表示用）。
                    has_secret = !string.IsNullOrEmpty(c.ClientSecret),
                    app_name = c.AppName,
                    is_sandbox = c.Organization?.IsSandbox ?? false,
                    organization_code = c.Organization?.Code,
                    organization_name = c.Organization?.Name,
                    redirect_uris = c.RedirectUris.Select(r => r.Uri).ToArray(),
                    allowed_rp_ids = c.AllowedRpIds.ToArray()
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
            var (client, subject, failure) = await ResolveOwnedClientAsync(id, "rotate secret");
            if (failure != null)
            {
                return failure;
            }

            var newSecret = GenerateClientSecret();
            client!.ClientSecret = await _secretProtector.ProtectAsync(newSecret);
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
        /// POST /v1/account/clients/{id}/secret/reveal
        /// 指定 Client の client_secret を 1 件だけ復号して返す。ユーザーが「表示」を
        /// 明示操作したときのみ呼ばれる想定で、一覧 API では secret を返さない。
        ///
        /// 参照系だが副作用（復号・監査ログ）を伴い、URL や履歴に残さないため POST とする。
        /// </summary>
        [HttpPost("clients/{id:int}/secret/reveal")]
        public async Task<IActionResult> RevealSecret(int id)
        {
            var (client, subject, failure) = await ResolveOwnedClientAsync(id, "reveal secret");
            if (failure != null)
            {
                return failure;
            }

            var revealed = string.IsNullOrEmpty(client!.ClientSecret)
                ? string.Empty
                : await _secretProtector.UnprotectAsync(client.ClientSecret);

            _logger.LogInformation("client_secret revealed for client {ClientId} by account {Subject}", client.ClientId, subject);

            return Ok(new
            {
                id = client.Id,
                client_id = client.ClientId,
                client_secret = revealed
            });
        }

        /// <summary>
        /// POST /v1/account/clients/{id}/redirect-uris
        /// 指定 Client の redirect_uri をリストごと全置換する。
        ///
        /// PUT ではなく POST なのは、CORS ポリシー <c>SignupApiCors</c> が GET / POST / OPTIONS
        /// 限定のため（Program.cs）。プリフライトで PUT / DELETE を通す設定変更をせずに済むよう、
        /// 部分更新ではなくリスト全置換のセマンティクスで組む。
        /// </summary>
        [HttpPost("clients/{id:int}/redirect-uris")]
        public async Task<IActionResult> UpdateRedirectUris(int id, [FromBody] RedirectUrisDto? body)
        {
            var (client, subject, failure) = await ResolveOwnedClientAsync(id, "update redirect_uris");
            if (failure != null)
            {
                return failure;
            }

            var (uris, invalid) = NormalizeRedirectUris(body?.RedirectUris);
            if (invalid != null)
            {
                return invalid;
            }

            // 全置換。既存行を消してから入れ直す（RedirectUri は Client 従属で他から参照されない）。
            var existing = await _context.RedirectUris
                .IgnoreQueryFilters()
                .Where(r => r.ClientId == client!.Id)
                .ToListAsync();
            _context.RedirectUris.RemoveRange(existing);

            var now = DateTimeOffset.UtcNow;
            foreach (var uri in uris!)
            {
                _context.RedirectUris.Add(new RedirectUri
                {
                    ClientId = client!.Id,
                    Uri = uri,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            client!.UpdatedAt = now;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "redirect_uris replaced for client {ClientId} by account {Subject} (count {Count})",
                client.ClientId, subject, uris.Count);

            return Ok(new
            {
                id = client.Id,
                client_id = client.ClientId,
                redirect_uris = uris.ToArray()
            });
        }

        /// <summary>
        /// POST /v1/account/clients/{id}/allowed-rp-ids
        /// 指定 Client の allowed_rp_ids をリストごと全置換する。POST を使う理由は
        /// <see cref="UpdateRedirectUris"/> と同じ。
        ///
        /// 自分が管理していないドメインを RP ID に入れても攻撃には使えない（WebAuthn は
        /// origin と RP ID の一致を要求するため、そのドメインの origin でページを配信できない
        /// 限りセレモニーが成立せず、作られる資格情報も自 Organization に閉じる）ため、
        /// 所有権の確認は課さない。ただし変更は監査ログに残す。
        /// </summary>
        [HttpPost("clients/{id:int}/allowed-rp-ids")]
        public async Task<IActionResult> UpdateAllowedRpIds(int id, [FromBody] AllowedRpIdsDto? body)
        {
            var (client, subject, failure) = await ResolveOwnedClientAsync(id, "update allowed_rp_ids");
            if (failure != null)
            {
                return failure;
            }

            var (rpIds, invalid) = NormalizeAllowedRpIds(body?.AllowedRpIds);
            if (invalid != null)
            {
                return invalid;
            }

            // getter は毎回新しいリストを返すため Add では永続化されない。リストごと再代入する。
            client!.AllowedRpIds = rpIds!;
            client.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "allowed_rp_ids replaced for client {ClientId} by account {Subject} (count {Count})",
                client.ClientId, subject, rpIds!.Count);

            return Ok(new
            {
                id = client.Id,
                client_id = client.ClientId,
                allowed_rp_ids = rpIds.ToArray()
            });
        }

        // ---- サイト（Organization）の一覧・追加・削除 ----

        /// <summary>
        /// GET /v1/account/organizations
        /// 呼び出し Account が管理するサイト（Organization）の一覧を返す。
        ///
        /// 論理削除済みのサイトは含まない（<see cref="IAccountService.GetManagedOrganizationsAsync"/>
        /// が除外する）。本番とテストの対応は <c>parent_organization_id</c> で表現し、
        /// UI 側はこれを使って本番の下にテストをぶら下げて表示する。
        /// </summary>
        [HttpGet("organizations")]
        public async Task<IActionResult> GetOrganizations()
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
            var maxSites = await GetMaxSitesAsync(subject);

            if (orgIds.Count == 0)
            {
                return Ok(new
                {
                    organizations = Array.Empty<object>(),
                    max_sites = maxSites,
                    production_site_count = 0
                });
            }

            // 管理対象 Organization は顧客テナント（別テナント）のため IgnoreQueryFilters で横断取得する。
            // 削除済みは orgIds の時点で除外済み。
            var organizations = await _context.Organizations
                .IgnoreQueryFilters()
                .Where(o => orgIds.Contains(o.Id))
                .OrderBy(o => o.Id)
                .ToListAsync();

            var clients = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.RedirectUris)
                .Where(c => c.OrganizationId != null && orgIds.Contains(c.OrganizationId.Value))
                .ToListAsync();

            var roleBySubject = managed.ToDictionary(m => m.OrganizationId, m => m.Role);

            var result = organizations.Select(o => new
            {
                id = o.Id,
                code = o.Code,
                name = o.Name,
                is_sandbox = o.IsSandbox,
                parent_organization_id = o.ParentOrganizationId,
                role = roleBySubject.TryGetValue(o.Id, out var role) ? role : null,
                created_at = o.CreatedAt,
                clients = clients
                    .Where(c => c.OrganizationId == o.Id)
                    .Select(c => new
                    {
                        id = c.Id,
                        client_id = c.ClientId,
                        // 値そのものは返さない（一覧に secret を載せない方針。RevealSecret を使う）。
                        has_secret = !string.IsNullOrEmpty(c.ClientSecret),
                        app_name = c.AppName,
                        redirect_uris = c.RedirectUris.Select(r => r.Uri).ToArray(),
                        allowed_rp_ids = c.AllowedRpIds.ToArray()
                    })
                    .ToArray()
            }).ToArray();

            return Ok(new
            {
                organizations = result,
                max_sites = maxSites,
                production_site_count = organizations.Count(o => !o.IsSandbox)
            });
        }

        /// <summary>
        /// POST /v1/account/organizations
        /// サイト（Organization + Client + RsaKeyPair + AccountOrganization）を 1 件追加する。
        ///
        /// <para>
        /// 申込で本番・テストの片方しか登録しなかった場合の復旧手段であり、サイトを増やす唯一の
        /// 動線でもある（EcAuth#482）。生成ロジックは申込フローと同じ
        /// <see cref="IOrganizationProvisioningService"/> を通す。
        /// </para>
        /// <para>
        /// 制約は 2 つ。(1) 本番サイトは 1 アカウントあたり <c>account.max_sites</c> 件まで。
        /// (2) テストサイトは本番 1 件につき 1 件まで（<c>parent_organization_id</c> で紐づけ、
        /// DB のフィルター付きユニークインデックスでも担保）。
        /// </para>
        /// </summary>
        [HttpPost("organizations")]
        public async Task<IActionResult> CreateOrganization([FromBody] CreateOrganizationDto? body)
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

            if (body == null || string.IsNullOrWhiteSpace(body.SiteUrl))
            {
                return InvalidInput("invalid_request", "site_url を指定してください。", "site_url");
            }

            var ecCubeVersion = NormalizeEcCubeVersion(body.EcCubeVersion);
            if (ecCubeVersion == null)
            {
                return InvalidInput(
                    "unsupported_version", "対応していない EC-CUBE バージョンです。", "ec_cube_version");
            }

            var account = await _context.Accounts
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.Subject == subject);
            if (account == null)
            {
                return Unauthorized(new
                {
                    error = "invalid_token",
                    error_description = "有効な Account アクセストークンが必要です。"
                });
            }

            var managed = await _accountService.GetManagedOrganizationsAsync(subject);
            var orgIds = managed.Select(m => m.OrganizationId).ToHashSet();

            // 削除済みは orgIds に含まれないため、以降のカウント・親検索はすべて有効な Org のみが対象。
            var ownedOrganizations = orgIds.Count == 0
                ? new List<Organization>()
                : await _context.Organizations
                    .IgnoreQueryFilters()
                    .Where(o => orgIds.Contains(o.Id))
                    .ToListAsync();

            int? parentOrganizationId = null;
            if (body.IsSandbox)
            {
                if (body.ParentOrganizationId == null)
                {
                    return InvalidInput(
                        "invalid_request",
                        "テストサイトを追加するには、紐づける本番サイト（parent_organization_id）を指定してください。",
                        "parent_organization_id");
                }

                var parent = ownedOrganizations.FirstOrDefault(o => o.Id == body.ParentOrganizationId.Value);
                if (parent == null || parent.IsSandbox)
                {
                    // 管理外の Org を指定された場合も、テスト Org を親に指定された場合も同じ扱い。
                    // 他アカウントの Organization の存在を漏らさないため理由は区別しない。
                    return InvalidInput(
                        "invalid_parent",
                        "紐づける本番サイトが見つかりません。",
                        "parent_organization_id");
                }

                var sandboxExists = ownedOrganizations.Any(o => o.ParentOrganizationId == parent.Id);
                if (sandboxExists)
                {
                    return InvalidInput(
                        "sandbox_already_exists",
                        "この本番サイトには既にテストサイトが登録されています。"
                            + "作り直す場合は既存のテストサイトを削除してから追加してください。",
                        "parent_organization_id");
                }

                parentOrganizationId = parent.Id;
            }
            else
            {
                // 本番サイトのみを上限の対象にする。テストサイトは本番 1 件につき 1 件までという
                // 別の制約で縛られるため、二重に数えない。
                var productionCount = ownedOrganizations.Count(o => !o.IsSandbox);
                if (productionCount >= account.MaxSites)
                {
                    return InvalidInput(
                        "site_limit_exceeded",
                        $"登録できる本番サイトは {account.MaxSites} 件までです。"
                            + "不要なサイトを削除するか、サポートにお問い合わせください。",
                        "site_url");
                }
            }

            SiteEntry site;
            try
            {
                site = _provisioning.BuildSite(body.SiteUrl, body.IsSandbox, "site_url");
                await _provisioning.EnsureOrganizationCodesAvailableAsync(
                    new[] { site }, HttpContext.RequestAborted, ownedOrganizationIds: orgIds);
            }
            catch (SignupValidationException ex)
            {
                return FromValidationException(ex);
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(HttpContext.RequestAborted);
            ProvisionedSite provisioned;
            try
            {
                provisioned = await _provisioning.ProvisionAsync(
                    site,
                    account.DisplayName ?? site.Host,
                    ecCubeVersion,
                    subject,
                    parentOrganizationId,
                    HttpContext.RequestAborted);

                await transaction.CommitAsync(HttpContext.RequestAborted);
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync(HttpContext.RequestAborted);

                // 事前チェックをすり抜けた並行追加（同じ組織コード / 同じ親へのテストサイト 2 件）。
                _logger.LogWarning(ex,
                    "サイト追加が競合しました: Subject={Subject}, Code={Code}", subject, site.Code);
                return Conflict(new
                {
                    error = "organization_already_exists",
                    error_description = "サイトの登録が競合しました。時間をおいて再度お試しください。",
                    field = "site_url"
                });
            }

            _logger.LogInformation(
                "サイトを追加しました: Subject={Subject}, OrganizationId={OrganizationId}, Code={Code}, IsSandbox={IsSandbox}, ParentOrganizationId={ParentOrganizationId}",
                subject, provisioned.Organization.Id, provisioned.Organization.Code,
                provisioned.Organization.IsSandbox, parentOrganizationId);

            // client_secret は返さない（一覧に載せないのと同じ理由）。UI は reveal で 1 件ずつ取得する。
            return Created($"/v1/account/organizations/{provisioned.Organization.Id}", new
            {
                id = provisioned.Organization.Id,
                code = provisioned.Organization.Code,
                name = provisioned.Organization.Name,
                is_sandbox = provisioned.Organization.IsSandbox,
                parent_organization_id = provisioned.Organization.ParentOrganizationId,
                created_at = provisioned.Organization.CreatedAt,
                client = new
                {
                    id = provisioned.Client.Id,
                    client_id = provisioned.Client.ClientId,
                    has_secret = !string.IsNullOrEmpty(provisioned.Client.ClientSecret),
                    redirect_uris = provisioned.Client.RedirectUris!.Select(r => r.Uri).ToArray(),
                    allowed_rp_ids = provisioned.Client.AllowedRpIds.ToArray()
                }
            });
        }

        /// <summary>
        /// POST /v1/account/organizations/{id}/delete
        /// サイトを論理削除する。
        ///
        /// <para>
        /// <c>DELETE</c> ではなく POST なのは CORS ポリシー <c>SignupApiCors</c> が
        /// GET / POST / OPTIONS 限定のため（<see cref="UpdateRedirectUris"/> と同じ理由）。
        /// またレコードを消さない以上、DELETE のセマンティクスとも合わない。
        /// </para>
        /// <para>
        /// **物理削除はしない**。将来の課金は Organization 単位の集計になるため、解約済みサイトも
        /// 残す必要がある。組織コードも解放しない（同じコードで作り直せると集計で別サイトの
        /// 利用期間が混ざる）ため、削除したドメインは再登録できない。
        /// </para>
        /// <para>
        /// 本番サイトを削除すると、紐づくテストサイトも同時に論理削除する。親だけ消えて
        /// 孤立したテストサイトが残ると、UI 上どの本番に属するか分からなくなるため。
        /// </para>
        /// </summary>
        [HttpPost("organizations/{id:int}/delete")]
        public async Task<IActionResult> DeleteOrganization(int id)
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

            // 削除済みの Org は管理対象から外れるため、二重削除もここで 404 になる。
            // 存在しない Org と管理外の Org も同じ 404 に揃える（存在を漏らさない）。
            if (!orgIds.Contains(id))
            {
                _logger.LogWarning(
                    "Account {Subject} attempted to delete organization {OrganizationId} without ownership",
                    subject, id);
                return NotFound(new
                {
                    error = "not_found",
                    error_description = "対象のサイトが見つかりません。"
                });
            }

            var owned = await _context.Organizations
                .IgnoreQueryFilters()
                .Where(o => orgIds.Contains(o.Id))
                .ToListAsync();

            var target = owned.First(o => o.Id == id);

            // 本番サイトの配下にあるテストサイトも巻き込んで削除する。
            var targets = new List<Organization> { target };
            if (!target.IsSandbox)
            {
                targets.AddRange(owned.Where(o => o.ParentOrganizationId == target.Id));
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var organization in targets)
            {
                organization.DeletedAt = now;
                organization.UpdatedAt = now;
            }

            await _context.SaveChangesAsync(HttpContext.RequestAborted);

            _logger.LogInformation(
                "サイトを削除しました（論理削除）: Subject={Subject}, OrganizationIds={OrganizationIds}",
                subject, string.Join(",", targets.Select(o => o.Id)));

            return Ok(new
            {
                deleted_organization_ids = targets.Select(o => o.Id).ToArray(),
                deleted_at = now
            });
        }

        /// <summary>
        /// 呼び出し Account の本番サイト上限を返す。Account が引けない場合は既定値を返す
        /// （一覧表示のための補助情報であり、実際の上限判定は追加時に Account を引いて行う）。
        /// </summary>
        private async Task<int> GetMaxSitesAsync(string subject)
        {
            var maxSites = await _context.Accounts
                .IgnoreQueryFilters()
                .Where(a => a.Subject == subject)
                .Select(a => (int?)a.MaxSites)
                .FirstOrDefaultAsync();

            return maxSites ?? Account.DefaultMaxSites;
        }

        /// <summary>
        /// ec_cube_version を検証する。未指定は 4 系として扱う（現行の主流であり、
        /// 2 系のコールバックパスとの違いは追加後に redirect_uri 編集で直せる）。
        /// 対応していない値は null を返す。
        /// </summary>
        private static string? NormalizeEcCubeVersion(string? version)
        {
            var v = version?.Trim();
            if (string.IsNullOrEmpty(v))
            {
                return "4";
            }

            return v is "2" or "4" or "other" ? v : null;
        }

        /// <summary>
        /// プロビジョニング側のバリデーション例外を、この API のエラー形式に変換する。
        /// </summary>
        private IActionResult FromValidationException(SignupValidationException ex)
        {
            return StatusCode(ex.StatusCode, new
            {
                error = ex.Error,
                error_description = ex.ErrorDescription,
                field = ex.Field
            });
        }

        // ---- 入力の正規化・検証 ----

        /// <summary>
        /// redirect_uri のリストを検証・正規化する。空要素は捨て、重複は先勝ちで畳む。
        ///
        /// authenticate/verify は登録値と**序数完全一致**で比較する（B2BPasskeyController）。
        /// 一方 EC-CUBE プラグインが組み立てる URL のホストは Host ヘッダ由来で常に小文字なので、
        /// scheme とホストだけ小文字＋Punycode に寄せ、パス・クエリは入力のまま残す
        /// （パスは大文字小文字を区別するため勝手に畳まない）。
        /// </summary>
        private (List<string>? Uris, IActionResult? Failure) NormalizeRedirectUris(List<string>? input)
        {
            if (input == null)
            {
                return (null, InvalidInput("invalid_request", "redirect_uris を配列で指定してください。", "redirect_uris"));
            }

            var result = new List<string>();
            for (var i = 0; i < input.Count; i++)
            {
                // 入力値そのものはエラーに載せない。redirect_uri は user:pass@ を含みうるので、
                // 反映するとブラウザのエラーレポートやプロキシのログにパスワードが残る。
                // 代わりに入力配列での位置（1 始まり）を返し、どの欄かは呼び出し側で分かるようにする。
                var position = i + 1;
                var trimmed = input[i]?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                if (trimmed.Length > MaxRedirectUriLength)
                {
                    return (null, InvalidInput(
                        "invalid_redirect_uri",
                        $"{position} 件目の redirect_uri が長すぎます（{MaxRedirectUriLength} 文字以内）。",
                        "redirect_uris"));
                }

                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(uri.Host))
                {
                    return (null, InvalidInput(
                        "invalid_redirect_uri",
                        $"{position} 件目の redirect_uri は https:// で始まる正しい URL を指定してください。",
                        "redirect_uris"));
                }

                // RFC 6749 Section 3.1.2: リダイレクト先にフラグメントは含められない。
                if (!string.IsNullOrEmpty(uri.Fragment))
                {
                    return (null, InvalidInput(
                        "invalid_redirect_uri",
                        $"{position} 件目の redirect_uri にフラグメント（#）は指定できません。",
                        "redirect_uris"));
                }

                // userinfo 付き URL は表示上のホストを偽装できるため受け付けない。
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    return (null, InvalidInput(
                        "invalid_redirect_uri",
                        $"{position} 件目の redirect_uri にユーザー情報（user:pass@）は指定できません。",
                        "redirect_uris"));
                }

                var authority = uri.IsDefaultPort
                    ? uri.IdnHost.ToLowerInvariant()
                    : $"{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
                var normalized = $"https://{authority}{uri.PathAndQuery}";

                // 保存されるのは正規化後の値。IDN の Punycode 化やパスのパーセントエンコードで
                // 入力より伸びうるため、上限は正規化後にも当てる。
                if (normalized.Length > MaxRedirectUriLength)
                {
                    return (null, InvalidInput(
                        "invalid_redirect_uri",
                        $"{position} 件目の redirect_uri が長すぎます（正規化後 {MaxRedirectUriLength} 文字以内）。",
                        "redirect_uris"));
                }

                if (!result.Contains(normalized, StringComparer.Ordinal))
                {
                    result.Add(normalized);
                }
            }

            if (result.Count == 0)
            {
                return (null, InvalidInput(
                    "invalid_redirect_uri",
                    "redirect_uri を 1 件以上指定してください。空にすると認証が完了できなくなります。",
                    "redirect_uris"));
            }

            if (result.Count > MaxRedirectUris)
            {
                return (null, InvalidInput(
                    "invalid_redirect_uri",
                    $"redirect_uri は {MaxRedirectUris} 件までです。",
                    "redirect_uris"));
            }

            return (result, null);
        }

        /// <summary>
        /// allowed_rp_ids のリストを検証・正規化する。空要素は捨て、重複は先勝ちで畳む。
        ///
        /// RP ID は WebAuthn の valid domain string（登録可能ドメイン名）でなければならない。
        /// スキーム・ポート・パスを含む文字列や IP アドレスは <c>navigator.credentials.*</c> の
        /// 時点で必ず失敗するため、保存前に弾いて設定ミスを即時に返す。
        /// </summary>
        private (List<string>? RpIds, IActionResult? Failure) NormalizeAllowedRpIds(List<string>? input)
        {
            if (input == null)
            {
                return (null, InvalidInput("invalid_request", "allowed_rp_ids を配列で指定してください。", "allowed_rp_ids"));
            }

            var result = new List<string>();
            for (var i = 0; i < input.Count; i++)
            {
                // redirect_uri 側と同じ理由で入力値は載せない（"user:pass@host" のような
                // 資格情報を含む文字列もここに到達しうる）。位置だけを返す。
                var position = i + 1;
                var trimmed = input[i]?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var normalized = NormalizeRpId(trimmed);
                if (normalized == null)
                {
                    return (null, InvalidInput(
                        "invalid_rp_id",
                        $"{position} 件目の RP ID はドメイン名だけを指定してください"
                            + "（スキーム・ポート・パス・IP アドレスは不可）。",
                        "allowed_rp_ids"));
                }

                if (!result.Contains(normalized, StringComparer.Ordinal))
                {
                    result.Add(normalized);
                }
            }

            if (result.Count == 0)
            {
                return (null, InvalidInput(
                    "invalid_rp_id",
                    "RP ID を 1 件以上指定してください。空にするとパスキーが使えなくなります。",
                    "allowed_rp_ids"));
            }

            if (result.Count > MaxAllowedRpIds)
            {
                return (null, InvalidInput(
                    "invalid_rp_id",
                    $"RP ID は {MaxAllowedRpIds} 件までです。",
                    "allowed_rp_ids"));
            }

            // 保存先カラムの上限を超えると SQL Server 側で失敗するため、シリアライズ後の長さで確認する。
            if (JsonSerializer.Serialize(result).Length > MaxAllowedRpIdsJsonLength)
            {
                return (null, InvalidInput(
                    "invalid_rp_id",
                    "RP ID の合計が長すぎます。件数を減らしてください。",
                    "allowed_rp_ids"));
            }

            return (result, null);
        }

        /// <summary>
        /// RP ID を小文字 Punycode に正規化する。valid domain string でなければ null を返す。
        /// ブラウザが送る Host ヘッダは Punycode なので、IDN は ASCII に寄せてから保存する
        /// （<c>SignupService</c> が初期値を作るときと同じ扱い）。
        /// </summary>
        private static string? NormalizeRpId(string value)
        {
            if (value.Length > MaxRpIdLength)
            {
                return null;
            }

            string ascii;
            try
            {
                ascii = new IdnMapping().GetAscii(value).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                // IdnMapping は空ラベル・不正文字・長すぎるラベルで ArgumentException を投げる。
                return null;
            }

            // 末尾ドット（絶対 FQDN 表記）とラベルの空要素は WebAuthn の RP ID として使えない。
            if (ascii.StartsWith('.') || ascii.EndsWith('.') || ascii.Contains(".."))
            {
                return null;
            }

            // Dns 以外（IPv4 / IPv6 / 解釈不能）はすべて拒否する。
            // "example.jp:443" や "https://example.jp" は Unknown になるのでここで落ちる。
            return Uri.CheckHostName(ascii) == UriHostNameType.Dns ? ascii : null;
        }

        /// <summary>入力エラーを申込 API と同じ形（error / error_description / field）で返す。</summary>
        private IActionResult InvalidInput(string error, string description, string field)
        {
            return UnprocessableEntity(new
            {
                error,
                error_description = description,
                field
            });
        }

        /// <summary>
        /// <c>POST /v1/account/clients/{id}/redirect-uris</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class RedirectUrisDto
        {
            [JsonPropertyName("redirect_uris")]
            public List<string>? RedirectUris { get; set; }
        }

        /// <summary>
        /// <c>POST /v1/account/clients/{id}/allowed-rp-ids</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class AllowedRpIdsDto
        {
            [JsonPropertyName("allowed_rp_ids")]
            public List<string>? AllowedRpIds { get; set; }
        }

        /// <summary>
        /// <c>POST /v1/account/organizations</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class CreateOrganizationDto
        {
            /// <summary>追加するサイトの URL（https 必須）。</summary>
            [JsonPropertyName("site_url")]
            public string? SiteUrl { get; set; }

            /// <summary>テストサイトとして追加する場合 true。</summary>
            [JsonPropertyName("is_sandbox")]
            public bool IsSandbox { get; set; }

            /// <summary>
            /// テストサイトを紐づける本番サイトの Organization Id。
            /// <c>is_sandbox = true</c> のときのみ必須。
            /// </summary>
            [JsonPropertyName("parent_organization_id")]
            public int? ParentOrganizationId { get; set; }

            /// <summary>
            /// 初期 redirect_uri のコールバックパスを決める（"2" / "4" / "other"）。
            /// 未指定は "4"。
            /// </summary>
            [JsonPropertyName("ec_cube_version")]
            public string? EcCubeVersion { get; set; }
        }

        /// <summary>
        /// アクセストークンを検証し、指定 Client が呼び出し Account の管理対象 Organization に
        /// 属するかを確認する。問題があれば <c>failure</c> にそのまま返すべきレスポンスを詰めて返す。
        /// 存在しない Client と権限の無い Client はいずれも 404 に揃える（存在を漏らさない）。
        /// </summary>
        private async Task<(Client? Client, string? Subject, IActionResult? Failure)> ResolveOwnedClientAsync(
            int id, string operation)
        {
            var subject = await ValidateAccountTokenAsync();
            if (subject == null)
            {
                return (null, null, Unauthorized(new
                {
                    error = "invalid_token",
                    error_description = "有効な Account アクセストークンが必要です。"
                }));
            }

            IActionResult NotFoundResult()
            {
                _logger.LogWarning(
                    "Account {Subject} attempted to {Operation} for client {ClientDbId} without ownership",
                    subject, operation, id);
                return NotFound(new
                {
                    error = "not_found",
                    error_description = "対象の Client が見つかりません。"
                });
            }

            var managed = await _accountService.GetManagedOrganizationsAsync(subject);
            var orgIds = managed.Select(m => m.OrganizationId).ToHashSet();

            // 管理対象が無ければ所有権チェックは必ず失敗するため、DB を引かず 404 を返す。
            if (orgIds.Count == 0)
            {
                return (null, subject, NotFoundResult());
            }

            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == id);

            // 所有権チェック: 対象 Client の Organization が呼び出し Account の管理対象か
            if (client == null || client.OrganizationId == null || !orgIds.Contains(client.OrganizationId.Value))
            {
                return (null, subject, NotFoundResult());
            }

            return (client, subject, null);
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
