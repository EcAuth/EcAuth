using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IMagicLinkService" />
    public class MagicLinkService : IMagicLinkService
    {
        // トークンのバイト長（32 byte = 256 bit）。エントロピーはセキュリティパラメータのため
        // 設定化せず定数で固定する（MagicLinkOptions のコメント参照）。
        private const int TokenBytes = 32;

        // 設定キーのテナント部を環境変数名に使える形へ正規化する正規表現（SignupService と同方針）。
        // 環境変数名はハイフンを含められない（Azure Linux App Service が拒否）ため、
        // [A-Za-z0-9_] 以外を "_" に置換する（例: "stg-accounts" -> "stg_accounts"）。
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IAccountService _accountService;
        private readonly ITokenService _tokenService;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly MagicLinkOptions _options;
        private readonly ILogger<MagicLinkService> _logger;

        public MagicLinkService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IAccountService accountService,
            ITokenService tokenService,
            IEmailService emailService,
            IConfiguration configuration,
            IOptions<MagicLinkOptions> options,
            ILogger<MagicLinkService> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _accountService = accountService;
            _tokenService = tokenService;
            _emailService = emailService;
            _configuration = configuration;
            _options = options.Value;
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
                ExpiresAt = now + TimeSpan.FromMinutes(_options.TokenLifetimeMinutes),
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
                // ダミーのハッシュ計算で処理時間を近づける（PerformDummyHash の現状仕様・既知の制約を参照。
                // SendGrid の ms オーダーとは完全には揃わない。後続課題 EcAuthDocs#91 で追跡）。
                PerformDummyHash();
                return;
            }

            var magicLinkUrl = BuildMagicLinkUrl(token);
            using (TimingScope.Begin("send_email"))
            {
                // メール送信失敗（SendGrid 4xx/5xx・API キー未設定等で InvalidOperationException）を
                // 呼び出し元に伝播させると、登録済みメールのみ 500・未登録は 200 となり Account 存否が
                // 漏れる（Email enumeration）。送信失敗は内部に留め（ログのみ）、Account 存在有無に
                // 関わらず常に正常 return する。送信失敗の検知は運用ログに委ねる。
                try
                {
                    await _emailService.SendMagicLoginLinkAsync(account.Email, magicLinkUrl, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "マジックリンクメールの送信に失敗しました（enumeration 防止のため握りつぶし）: Tenant={Tenant}, TokenHash={TokenHash}",
                        _tenantService.TenantName, TokenHashPrefix(token));
                }
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

            // まずトークンを read で事前検証する（消費はまだ行わない）。Account / Client の解決を
            // 消費の前に済ませることで、設定ミス（login_not_configured）や Account 欠落で失敗しても
            // ワンタイムトークンを焼かない（設定を直せば配信済みリンクが再び使える）。
            // 最終的な単発保証は後段の Compare-And-Set 消費で行う。この事前 read と消費の間に別リクエストが
            // 割り込んでも、アトミックな UPDATE の影響行数で 1 件だけが成立する（二重ログインは起きない）。
            MagicLoginToken? magicToken;
            using (TimingScope.Begin("token_lookup"))
            {
                magicToken = await _context.MagicLoginTokens
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now, ct);
            }

            if (magicToken?.AccountSubject == null)
            {
                // 無効／期限切れ／使用済み／Account 不在（レート制限用の行）を区別せず同一エラーとする。
                _logger.LogWarning(
                    "マジックリンクトークンが無効です（無効／期限切れ／使用済み／Account 不在）: Tenant={Tenant}, TokenHash={TokenHash}",
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

            // ログイン先（管理コンソール）の Client を解決する。発行するトークンの audience になる。
            // ここまでを消費の前に行うため、解決失敗時にトークンは焼けない。
            var client = await ResolveAccountClientAsync(account.OrganizationId, ct);

            // 単発使用は Compare-And-Set で保証する。未使用かつ未期限切れの行のみを対象とする
            // アトミックな UPDATE を実行し、影響行数 1 を成功とする（並行リクエストはここで 1 件だけ通る）。
            int affected;
            using (TimingScope.Begin("token_consume"))
            {
                affected = await TryConsumeTokenAsync(tokenHash, now, ct);
            }

            if (affected != 1)
            {
                // 事前 read 後・消費前に別リクエストが消費した／期限切れになった等。同一エラーで返す。
                _logger.LogWarning(
                    "マジックリンクトークンの消費に失敗しました（並行使用／期限切れ）: Tenant={Tenant}, TokenHash={TokenHash}",
                    _tenantService.TenantName, TokenHashPrefix(token));
                throw InvalidToken();
            }

            // 認可コードを介さず、この場でトークンを発行する（IMagicLinkService.VerifyAsync の
            // ドキュメント参照。public client の PKCE 必須と両立させるための設計）。
            // managed_orgs はマイページが Client 一覧を引くために必須。
            var managedOrgs = await _accountService.GetManagedOrganizationsAsync(account.Subject);

            ITokenService.TokenResponse tokens;
            using (TimingScope.Begin("token_generate"))
            {
                tokens = await _tokenService.GenerateTokensAsync(new ITokenService.TokenRequest
                {
                    User = account,
                    Client = client,
                    SubjectType = SubjectType.Account,
                    ManagedOrgs = managedOrgs
                });
            }

            _logger.LogInformation(
                "マジックリンクログインを検証しトークンを発行しました: Tenant={Tenant}, Subject={Subject}, ClientId={ClientId}, ManagedOrgs={Count}",
                _tenantService.TenantName, account.Subject, client.ClientId, managedOrgs.Count);

            return new MagicLinkVerifyResult(
                tokens.AccessToken, tokens.IdToken, tokens.ExpiresIn, tokens.TokenType);
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
            // 同一メールはウィンドウ内に 1 回のみ（直近ウィンドウ内に 1 件でもあれば拒否）。
            var emailWindowStart = now - TimeSpan.FromMinutes(_options.EmailRateLimitWindowMinutes);
            var recentByEmail = await _context.MagicLoginTokens
                .CountAsync(t => t.RequestedEmailHash == emailHash && t.CreatedAt > emailWindowStart, ct);
            if (recentByEmail >= 1)
            {
                throw RateLimited();
            }

            // 同一 IP はウィンドウ内に上限回数まで。IP が取得できない場合は IP ベースの制限を適用しない。
            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                var ipWindowStart = now - TimeSpan.FromMinutes(_options.IpRateLimitWindowMinutes);
                var recentByIp = await _context.MagicLoginTokens
                    .CountAsync(t => t.RequestedIp == ipAddress && t.CreatedAt > ipWindowStart, ct);
                if (recentByIp >= _options.IpRateLimitMaxRequests)
                {
                    throw RateLimited();
                }
            }
        }

        // ---- Client 解決 ----

        /// <summary>
        /// 受付テナント（accounts / stg-accounts）の管理コンソール Client（<see cref="SubjectType.Account"/>）を
        /// 解決する。<see cref="Data.Seeders.AccountsOrganizationSeeder"/> が投入する Client に対応する。
        /// 認可コードを発行しなくなったため、リダイレクト URI の登録有無は問わない
        /// （発行するトークンの audience として Client のみを必要とする）。
        /// </summary>
        private async Task<Client> ResolveAccountClientAsync(int organizationId, CancellationToken ct)
        {
            // account は GetBySubjectAsync でテナント分離済み。その OrganizationId で Client を絞るため、
            // テナントクエリフィルター（将来 Client に追加された場合も含む）はそのまま尊重する。
            var client = await _context.Clients
                .FirstOrDefaultAsync(
                    c => c.OrganizationId == organizationId && c.SubjectType == SubjectType.Account, ct);

            if (client == null)
            {
                _logger.LogError(
                    "管理コンソール Client が見つかりません: Tenant={Tenant}, OrganizationId={OrganizationId}",
                    _tenantService.TenantName, organizationId);
                throw new MagicLinkException(
                    "login_not_configured",
                    "ログイン環境が正しく構成されていません。サポートにお問い合わせください。",
                    statusCode: 500);
            }

            return client;
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
        /// SHA-256 を 1 回計算して破棄する。
        /// <para>
        /// <strong>【現状仕様・既知の制約】タイミングサイドチャネルは完全には解消していない。</strong>
        /// Account 存在時はメール送信（SendGrid への HTTP ラウンドトリップ, 数十〜数百 ms）を伴う一方、
        /// 本ダミー計算は μs オーダーのため、レスポンスタイムを精密計測すると Account の存否を推測しうる
        /// （レビュー bot がこの点を繰り返し指摘するが、以下のとおり意図的な現状仕様）。
        /// </para>
        /// <para>
        /// 完全な均一化にはメール送信のバックグラウンド化（fire-and-forget）等の構造変更が必要だが、
        /// リスクは中〜低（漏れるのは存在判定の偵察のみで直接侵入ではない。レート制限
        /// （email 5 分 1 回 / IP 1 時間 10 回）が列挙速度を抑制）と評価し、本対応は見送る。
        /// HTTP 200 + 同一メッセージによる enumeration 対策は維持する。タイミング解消は後続課題として
        /// 追跡する: EcAuthDocs#91「マジックリンク request の Email enumeration タイミングサイドチャネル対策」。
        /// </para>
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
