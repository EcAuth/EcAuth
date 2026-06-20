using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IMagicLinkService" />
    public class MagicLinkService : IMagicLinkService
    {
        // マジックリンクトークンの有効期限（設計上 10 分）。
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

        // トークンのバイト長（32 byte = 256 bit）。
        private const int TokenBytes = 32;

        // レート制限: 同一メールは 5 分に 1 回。
        private static readonly TimeSpan EmailRateWindow = TimeSpan.FromMinutes(5);

        // レート制限: 同一 IP は 1 時間に 10 回。
        private static readonly TimeSpan IpRateWindow = TimeSpan.FromHours(1);
        private const int IpRateLimit = 10;

        // 設定キーのテナント部を環境変数名に使える形へ正規化する正規表現（SignupService と同方針）。
        // 環境変数名はハイフンを含められない（Azure Linux App Service が拒否）ため、
        // [A-Za-z0-9_] 以外を "_" に置換する（例: "stg-accounts" -> "stg_accounts"）。
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IAccountService _accountService;
        private readonly IAuthorizationCodeService _authorizationCodeService;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MagicLinkService> _logger;

        public MagicLinkService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IAccountService accountService,
            IAuthorizationCodeService authorizationCodeService,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<MagicLinkService> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _accountService = accountService;
            _authorizationCodeService = authorizationCodeService;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task RequestAsync(string? email, string? ipAddress, string? userAgent, CancellationToken ct = default)
        {
            var normalizedEmail = NormalizeEmail(email);

            // 形式不正なメールは Account の存在判定とは無関係なため記録もメール送信もしないが、
            // enumeration を漏らさないようダミーのハッシュ計算で処理時間を合わせて正常終了する。
            if (normalizedEmail == null)
            {
                PerformDummyHash();
                return;
            }

            var emailHash = HashEmail(normalizedEmail);
            var now = DateTimeOffset.UtcNow;

            // レート制限判定（Account 存在有無に関わらず同一閾値）。
            // MagicLoginToken はテナント横断（クエリフィルターなし）でカウントする。
            using (TimingScope.Begin("rate_limit"))
            {
                await EnforceRateLimitAsync(emailHash, ipAddress, now, ct);
            }

            // Account を検索する（テナントクエリフィルターにより現テナントの Account のみが対象）。
            Account? account;
            using (TimingScope.Begin("account_lookup"))
            {
                account = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.Email == normalizedEmail, ct);
            }

            // 生トークンとその SHA-256 ハッシュを生成する。
            // Account 不在のリクエストも、レート制限カウントの母数として MagicLoginToken に記録する
            //（設計書: Account 不在のリクエストも同テーブルに記録する）。token_hash は unique 制約があるため
            // 不在時もランダム値を保存する（誰も verify しない）。
            var token = GenerateToken();
            var tokenHash = HashToken(token);

            var magicToken = new MagicLoginToken
            {
                AccountSubject = account?.Subject,
                RequestedEmailHash = emailHash,
                TokenHash = tokenHash,
                ExpiresAt = now + TokenLifetime,
                RequestedIp = Truncate(ipAddress, 45),
                RequestedUserAgent = Truncate(userAgent, 1000),
                CreatedAt = now
            };

            using (TimingScope.Begin("persist"))
            {
                _context.MagicLoginTokens.Add(magicToken);
                await _context.SaveChangesAsync(ct);
            }

            // 平文トークンはログに出さない（ハッシュ先頭 8 文字のみ）。
            _logger.LogInformation(
                "マジックリンク発行リクエストを受け付けました: Tenant={Tenant}, AccountExists={AccountExists}, TokenHash={TokenHash}",
                _tenantService.TenantName, account != null, TokenHashPrefix(token));

            if (account == null)
            {
                // 存在しない宛先にはメールを送らない。送信処理ぶんの時間差で存在を推測されないよう
                // ダミーのハッシュ計算で処理時間を合わせる。
                PerformDummyHash();
                return;
            }

            var magicLinkUrl = BuildMagicLinkUrl(token);
            using (TimingScope.Begin("send_email"))
            {
                await _emailService.SendMagicLoginLinkAsync(account.Email, magicLinkUrl, ct);
            }
        }

        /// <inheritdoc />
        public async Task<MagicLinkVerifyResult> VerifyAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw InvalidToken();
            }

            var tokenHash = HashToken(token);
            var now = DateTimeOffset.UtcNow;

            // 単発使用は Compare-And-Set で保証する。read -> check -> update の 3 ステップでは
            // Race Condition により複数回ログインが成立しうるため、未使用かつ未期限切れの行のみを
            // 対象とするアトミックな UPDATE を実行し、影響行数 1 を成功とする（設計書のセキュリティ要件）。
            int affected;
            using (TimingScope.Begin("token_consume"))
            {
                affected = await TryConsumeTokenAsync(tokenHash, now, ct);
            }

            if (affected != 1)
            {
                // 無効／期限切れ／使用済みを区別せず同一エラーとする（情報を漏らさない）。
                _logger.LogWarning(
                    "マジックリンクトークンの消費に失敗しました（無効／期限切れ／使用済み）: Tenant={Tenant}, TokenHash={TokenHash}",
                    _tenantService.TenantName, TokenHashPrefix(token));
                throw InvalidToken();
            }

            // 消費に成功した行を取得する（ExecuteUpdate はトラッキングを経由しないため再取得する）。
            var magicToken = await _context.MagicLoginTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

            if (magicToken?.AccountSubject == null)
            {
                // Account 不在のリクエストで作られた行（レート制限カウント用）。通常は到達しない。
                _logger.LogWarning(
                    "消費したマジックリンクトークンに AccountSubject がありません: Tenant={Tenant}, TokenHash={TokenHash}",
                    _tenantService.TenantName, TokenHashPrefix(token));
                throw InvalidToken();
            }

            // Account を取得する。テナントクエリフィルターにより、別テナントで発行された
            // トークンを現テナントのホストで使おうとした場合は null になり弾かれる（クロステナント防止）。
            var account = await _accountService.GetBySubjectAsync(magicToken.AccountSubject);
            if (account == null)
            {
                _logger.LogWarning(
                    "マジックリンクトークンの Account が現テナントで見つかりません: Tenant={Tenant}, TokenHash={TokenHash}",
                    _tenantService.TenantName, TokenHashPrefix(token));
                throw InvalidToken();
            }

            // ログイン先（管理コンソール）の Client と登録済みリダイレクト URI を解決する。
            var (client, redirectUri) = await ResolveAccountClientAsync(account.OrganizationId, ct);

            var authCode = await _authorizationCodeService.GenerateAuthorizationCodeAsync(
                new IAuthorizationCodeService.AuthorizationCodeRequest
                {
                    Subject = account.Subject,
                    ClientId = client.Id,
                    RedirectUri = redirectUri,
                    SubjectType = SubjectType.Account,
                    ExpirationMinutes = 10
                });

            _logger.LogInformation(
                "マジックリンクログインを検証し認可コードを発行しました: Tenant={Tenant}, Subject={Subject}, ClientId={ClientId}",
                _tenantService.TenantName, account.Subject, client.ClientId);

            return new MagicLinkVerifyResult(AppendAuthorizationCode(redirectUri, authCode.Code));
        }

        /// <summary>
        /// トークンを単発消費する（Compare-And-Set）。未使用かつ未期限切れの行のみを対象に
        /// <c>used_at</c> を原子的に更新し、影響行数を返す（成功 = 1）。read → check → update の
        /// 3 ステップでは Race Condition により複数回ログインが成立しうるため、必ずアトミックな
        /// 単一 UPDATE で実装する（設計書のセキュリティ要件）。
        /// <para>
        /// この並行下のアトミック性は SQL Server の単一 UPDATE 文のセマンティクスに依存するため、
        /// 実 DB（統合テスト / E2E）で担保する。<c>ExecuteUpdate</c> は EF Core の InMemory
        /// プロバイダー非対応のため、ユニットテストではこのメソッドを override して逐次の単発契約のみ検証する。
        /// </para>
        /// </summary>
        protected virtual Task<int> TryConsumeTokenAsync(
            string tokenHash, DateTimeOffset now, CancellationToken ct)
        {
            return _context.MagicLoginTokens
                .Where(t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        }

        // ---- レート制限 ----

        private async Task EnforceRateLimitAsync(
            string emailHash, string? ipAddress, DateTimeOffset now, CancellationToken ct)
        {
            // 同一メールは 5 分に 1 回（直近ウィンドウ内に 1 件でもあれば拒否）。
            var emailWindowStart = now - EmailRateWindow;
            var recentByEmail = await _context.MagicLoginTokens
                .CountAsync(t => t.RequestedEmailHash == emailHash && t.CreatedAt > emailWindowStart, ct);
            if (recentByEmail >= 1)
            {
                throw RateLimited();
            }

            // 同一 IP は 1 時間に 10 回。IP が取得できない場合は IP ベースの制限を適用しない。
            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                var ipWindowStart = now - IpRateWindow;
                var recentByIp = await _context.MagicLoginTokens
                    .CountAsync(t => t.RequestedIp == ipAddress && t.CreatedAt > ipWindowStart, ct);
                if (recentByIp >= IpRateLimit)
                {
                    throw RateLimited();
                }
            }
        }

        // ---- Client / リダイレクト URI 解決 ----

        /// <summary>
        /// 受付テナント（accounts / stg-accounts）の管理コンソール Client（<see cref="SubjectType.Account"/>）と、
        /// その登録済みリダイレクト URI を解決する。<see cref="Data.Seeders.AccountsOrganizationSeeder"/> が
        /// 投入する Client / RedirectUri に対応する。
        /// </summary>
        private async Task<(Client client, string redirectUri)> ResolveAccountClientAsync(
            int organizationId, CancellationToken ct)
        {
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.RedirectUris)
                .FirstOrDefaultAsync(
                    c => c.OrganizationId == organizationId && c.SubjectType == SubjectType.Account, ct);

            var redirectUri = client?.RedirectUris?.FirstOrDefault()?.Uri;

            if (client == null || string.IsNullOrWhiteSpace(redirectUri))
            {
                _logger.LogError(
                    "管理コンソール Client またはリダイレクト URI が見つかりません: Tenant={Tenant}, OrganizationId={OrganizationId}",
                    _tenantService.TenantName, organizationId);
                throw new MagicLinkException(
                    "login_not_configured",
                    "ログイン環境が正しく構成されていません。サポートにお問い合わせください。",
                    statusCode: 500);
            }

            return (client, redirectUri);
        }

        /// <summary>
        /// リダイレクト URI に認可コードをクエリ <c>code</c> として付与する。
        /// 既にクエリを持つ URI でも壊さないよう区切り文字を判定する。
        /// </summary>
        private static string AppendAuthorizationCode(string redirectUri, string code)
        {
            var separator = redirectUri.Contains('?') ? '&' : '?';
            return $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}";
        }

        // ---- URL 構築 ----

        /// <summary>
        /// マジックリンクの URL を組み立てる。基底 URL はテナント別の信頼済み設定値
        /// <c>MagicLink:BaseUrl:{tenant_name}</c>（フロントエンドの配信元）からのみ取得する。
        /// Host ヘッダ偽装によるトークン窃取を防ぐため <c>Request.Host</c> へはフォールバックしない
        /// （SignupService.BuildConfirmUrl と同方針）。テナント名のハイフンは環境変数名に使えないため
        /// <c>[A-Za-z0-9_]</c> 以外を <c>_</c> に正規化する（例: <c>stg-accounts</c> → <c>stg_accounts</c>）。
        /// </summary>
        private string BuildMagicLinkUrl(string token)
        {
            var encodedToken = Uri.EscapeDataString(token);

            var tenantName = _tenantService.TenantName;
            var configKey = $"MagicLink:BaseUrl:{NonConfigKeyChar.Replace(tenantName, "_")}";
            var configuredBase = _configuration[configKey];

            if (string.IsNullOrWhiteSpace(configuredBase)
                || !Uri.TryCreate(configuredBase, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError(
                    "マジックリンク URL の基底が未設定または不正です: Tenant={Tenant}, Key={Key}",
                    tenantName, configKey);
                throw new InvalidOperationException(
                    $"マジックリンク URL の基底を決定できません。{configKey} に有効な https:// URL を設定してください。");
            }

            return $"{configuredBase.TrimEnd('/')}/signin/magic-link?token={encodedToken}";
        }

        // ---- トークン・ハッシュ・補助 ----

        private static string GenerateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        /// <summary>
        /// トークンを SHA-256 でハッシュ化し、16 進小文字（64 文字）で返す。
        /// 高エントロピーのランダム値のためソルトは不要。DB にはこのハッシュのみ保存する。
        /// </summary>
        private static string HashToken(string token)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
                .ToLowerInvariant();
        }

        /// <summary>
        /// 正規化済みメールアドレスを SHA-256 でハッシュ化し、16 進小文字（64 文字）で返す。
        /// レート制限の判定キー（<c>requested_email_hash</c>）に使用する。
        /// </summary>
        private static string HashEmail(string normalizedEmail)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)))
                .ToLowerInvariant();
        }

        /// <summary>
        /// Account 不在時やメール形式不正時に、処理時間を Account 存在時に近づけるためのダミー計算。
        /// SHA-256 を 1 回計算して破棄する（タイミング差による enumeration を緩和する）。
        /// </summary>
        private static void PerformDummyHash()
        {
            var dummy = RandomNumberGenerator.GetBytes(TokenBytes);
            _ = SHA256.HashData(dummy);
        }

        private static string TokenHashPrefix(string token)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        /// <summary>
        /// メールアドレスを正規化（trim + lowercase）する。空・255 超・形式不正は null を返す。
        /// </summary>
        private static string? NormalizeEmail(string? rawEmail)
        {
            var email = rawEmail?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(email) || email.Length > 255 || !IsValidEmail(email))
            {
                return null;
            }
            return email;
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return string.Equals(addr.Address, email, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private static MagicLinkException InvalidToken() => new(
            "invalid_token",
            "ログインリンクが無効か、有効期限が切れています。お手数ですが再度お試しください。",
            statusCode: 400);

        private static MagicLinkException RateLimited() => new(
            "rate_limited",
            "リクエストが多すぎます。しばらく時間をおいて再度お試しください。",
            statusCode: 429);
    }
}
