using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Telemetry;
using IdpUtilities.Security;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="ISignupService" />
    public class SignupService : ISignupService
    {
        // 確認トークンの有効期限（設計上 24 時間）。
        private static readonly TimeSpan ConfirmTokenLifetime = TimeSpan.FromHours(24);

        // 確認トークンのバイト長（32 byte = 256 bit）。
        private const int ConfirmTokenBytes = 32;

        // 同意バージョンの既定値（input で未指定の場合に使用）。
        private const string DefaultPolicyVersion = "1.0";

        // 確認 URL 設定キーのテナント部を環境変数名に使える形へ正規化する正規表現。
        // 環境変数名はハイフンを含められない（Azure Linux App Service が 400 で拒否）ため、
        // [A-Za-z0-9_] 以外を "_" に置換する（例: "stg-accounts" -> "stg_accounts"）。
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IEmailService _emailService;
        private readonly IDisposableEmailChecker _disposableEmailChecker;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SignupService> _logger;
        private readonly IPasskeyRegistrationTokenService _registrationTokenService;
        private readonly IOrganizationProvisioningService _provisioning;

        public SignupService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IEmailService emailService,
            IDisposableEmailChecker disposableEmailChecker,
            IConfiguration configuration,
            ILogger<SignupService> logger,
            IPasskeyRegistrationTokenService registrationTokenService,
            IOrganizationProvisioningService provisioning)
        {
            _context = context;
            _tenantService = tenantService;
            _emailService = emailService;
            _disposableEmailChecker = disposableEmailChecker;
            _configuration = configuration;
            _logger = logger;
            _registrationTokenService = registrationTokenService;
            _provisioning = provisioning;
        }

        /// <inheritdoc />
        public async Task<SignupRequest> RequestAsync(SignupInput input, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(input);

            string email;
            string organizationName;
            SiteSet sites;
            using (TimingScope.Begin("validate"))
            {
                // 入力の正規化とバリデーション（最初の違反で SignupValidationException をスロー）。
                email = ValidateAndNormalizeEmail(input.Email);
                organizationName = ValidateOrganizationName(input.OrganizationName);
                sites = ValidateSiteUrls(input.ProductionSiteUrl, input.TestSiteUrl);
                ValidateEcCubeVersion(input.EcCubeVersion);

                // 組織コードの重複チェック（全テナント横断）。
                await _provisioning.EnsureOrganizationCodesAvailableAsync(sites.Sites, ct);
            }

            // 生トークン（メール URL に使う）と、その SHA-256 ハッシュ（DB に保存する）を生成する。
            var confirmToken = GenerateConfirmToken();
            var confirmTokenHash = HashConfirmToken(confirmToken);
            var now = DateTimeOffset.UtcNow;

            var signupRequest = new SignupRequest
            {
                ConfirmTokenHash = confirmTokenHash,
                Email = email,
                OrganizationName = organizationName,
                ContactName = string.IsNullOrWhiteSpace(input.ContactName) ? null : input.ContactName.Trim(),
                ProductionSiteUrl = sites.ProductionUrl,
                TestSiteUrl = sites.TestUrl,
                EcCubeVersion = input.EcCubeVersion!.Trim(),
                TermsVersion = NormalizePolicyVersion(input.TermsVersion),
                PrivacyVersion = NormalizePolicyVersion(input.PrivacyVersion),
                CookieVersion = NormalizePolicyVersion(input.CookieVersion),
                TenantName = _tenantService.TenantName,
                ExpiresAt = now + ConfirmTokenLifetime,
                CreatedAt = now
            };

            using (TimingScope.Begin("persist"))
            {
                _context.SignupRequests.Add(signupRequest);
                await _context.SaveChangesAsync(ct);
            }

            // 平文トークンはログに出さない（ハッシュ先頭のみを参照可能にする）。
            _logger.LogInformation(
                "申込リクエストを受け付けました: Tenant={Tenant}, TokenHash={TokenHash}",
                signupRequest.TenantName, TokenHashPrefix(confirmToken));

            var confirmUrl = BuildConfirmUrl(confirmToken);
            using (TimingScope.Begin("send_email"))
            {
                await _emailService.SendSignupConfirmationAsync(email, organizationName, confirmUrl, ct);
            }

            return signupRequest;
        }

        /// <inheritdoc />
        public async Task<ISignupService.ConfirmResult> ConfirmAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new SignupValidationException(
                    "invalid_token", "確認トークンが指定されていません。", field: "token");
            }

            SignupRequest signupRequest;
            SiteSet sites;
            Organization accountsOrg;
            using (TimingScope.Begin("token_lookup"))
            {
                // 受信した生トークンを同じ方式でハッシュ化し、ConfirmTokenHash と照合する。
                // ConfirmTokenHash はグローバルユニークで、テナントコンテキストは Host から設定されるため、
                // グローバルクエリフィルター（TenantName）により現テナントの行のみが取得される。
                var tokenHash = HashConfirmToken(token);
                var foundRequest = await _context.SignupRequests
                    .FirstOrDefaultAsync(sr => sr.ConfirmTokenHash == tokenHash, ct);

                if (foundRequest == null)
                {
                    throw new SignupValidationException(
                        "invalid_token", "確認トークンが無効です。", field: "token");
                }
                signupRequest = foundRequest;

                if (signupRequest.ConfirmedAt != null)
                {
                    throw new SignupValidationException(
                        "already_confirmed", "この申込は既に確認済みです。", field: "token");
                }

                if (signupRequest.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new SignupValidationException(
                        "token_expired", "確認トークンの有効期限が切れています。お手数ですが再度お申し込みください。", field: "token");
                }

                // 本番サイト URL 必須化（EcAuth#482）より前に保存された申込は、本番 URL を
                // 持たないまま確認待ちになっている可能性がある。下の再バリデーションに任せると
                // 「本番サイト URL を入力してください」が返るが、確認画面には入力欄が無く
                // 利用者は何もできない。再申込しかないことが伝わるエラーに振り替える。
                //
                // 該当するのはこの変更のデプロイ前 24 時間（ConfirmTokenLifetime）以内に
                // テストサイトのみで申し込み、まだ確認していないケースに限られる。
                if (string.IsNullOrWhiteSpace(signupRequest.ProductionSiteUrl))
                {
                    _logger.LogWarning(
                        "本番サイト URL を持たない申込の確認を拒否しました: Tenant={Tenant}, TokenHash={TokenHash}",
                        signupRequest.TenantName, TokenHashPrefix(token));
                    throw new SignupValidationException(
                        "signup_needs_resubmission",
                        "お申し込み内容が現在の登録要件を満たしていません。"
                            + "お手数ですが、本番サイト URL を入力して再度お申し込みください。",
                        field: "token",
                        statusCode: 422);
                }

                // 申込時から confirm までの間にデータが変わっている可能性があるため、再バリデーションする。
                sites = ValidateSiteUrls(signupRequest.ProductionSiteUrl, signupRequest.TestSiteUrl);
                // confirm 時の code 衝突は Race Condition のため 409 を返す。
                await _provisioning.EnsureOrganizationCodesAvailableAsync(sites.Sites, ct, statusCode: 409);

                // 受付テナント（accounts / stg-accounts）の Organization を取得する。
                // 受付 Org の code は tenant_name と一致する（AccountsOrganizationSeeder の定義）。
                var foundAccountsOrg = await _context.Organizations
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        o => o.TenantName == signupRequest.TenantName && o.Code == signupRequest.TenantName, ct);

                if (foundAccountsOrg == null)
                {
                    _logger.LogError(
                        "受付テナントの Organization が見つかりません: Tenant={Tenant}", signupRequest.TenantName);
                    throw new SignupValidationException(
                        "tenant_not_configured",
                        "申込受付環境が正しく構成されていません。サポートにお問い合わせください。",
                        statusCode: 500);
                }
                accountsOrg = foundAccountsOrg;

                // 新規 Account 作成フローでは同一メールでの複数 org を許容しない方針
                //（org 招待・追加の動線は将来バージョンで対応）。受付テナント Org に
                // 同一メールの Account が既存なら、URL 変更では解決しない旨が伝わる明確なエラーで弾く。
                // Account のクエリフィルター（所属 Org の TenantName 一致）に依存しないよう
                // IgnoreQueryFilters() で受付 Org を明示的に絞り込む。
                var emailAlreadyRegistered = await _context.Accounts
                    .IgnoreQueryFilters()
                    .AnyAsync(a => a.OrganizationId == accountsOrg.Id && a.Email == signupRequest.Email, ct);
                if (emailAlreadyRegistered)
                {
                    throw new SignupValidationException(
                        "email_already_registered",
                        "このメールアドレスは既に登録されています。",
                        field: "email",
                        statusCode: 409);
                }
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(ct);
            using (TimingScope.Begin("confirm"))
            {
            try
            {
                var subject = Guid.NewGuid().ToString();

                // Account（受付テナント Org 所属）。
                var account = new Account
                {
                    Subject = subject,
                    Email = signupRequest.Email,
                    OrganizationId = accountsOrg.Id,
                    EmailVerifiedAt = DateTimeOffset.UtcNow
                };
                _context.Accounts.Add(account);

                // B2BUser（Subject を Account と共有、external_id=SHA-256(email)、受付テナント Org 所属）。
                // external_id は個人情報を含むため正規化 + ハッシュ化して保持する（Account.email は表示用に平文保持）。
                var b2bUser = new B2BUser
                {
                    Subject = subject,
                    ExternalId = ExternalIdHasher.Hash(signupRequest.Email),
                    UserType = "account_owner",
                    OrganizationId = accountsOrg.Id
                };
                _context.B2BUsers.Add(b2bUser);

                // 発行元ごとの識別子（EcAuthDocs#110）。発行元は受付テナントの管理コンソール
                // Client（SubjectType.Account）。accounts と stg-accounts は別 Organization なので、
                // 固定値ではなく client_id を使うことで同一人物が両方に申し込んでも衝突しない。
                var accountsClientId = await _context.Clients
                    .IgnoreQueryFilters()
                    .Where(c => c.OrganizationId == accountsOrg.Id && c.SubjectType == SubjectType.Account)
                    .Select(c => c.ClientId)
                    .FirstOrDefaultAsync(ct);

                if (accountsClientId == null)
                {
                    // identity 無しでも b2b_user.external_id 経由のフォールバックで解決できるため、
                    // 申込自体は継続する（移行前データと同じ状態になる）。
                    _logger.LogWarning(
                        "B2BUserIdentity の作成をスキップしました: 受付テナントに Account 型 Client がありません Tenant={Tenant}",
                        signupRequest.TenantName);
                }
                else
                {
                    _context.B2BUserIdentities.Add(new B2BUserIdentity
                    {
                        B2BSubject = subject,
                        IssuerKey = B2BIssuerKey.ForClient(accountsClientId),
                        ExternalId = b2bUser.ExternalId,
                        ClientId = accountsClientId
                    });
                }

                // 顧客 Organization を入力 URL に応じて 1〜2 件作成し、
                // 各 Org に Client / RsaKeyPair / AccountOrganization を作成する。
                //
                // 本番を先に作り、テスト Org には本番の Id を親として持たせる
                //（「1 本番 Org あたりサンドボックスは 1 つ」の紐付け）。テストサイトだけで
                // 申し込んだ場合は親が存在しないため null のままにする。後から本番サイトを
                // 追加したときに、マイページ側でこの孤立サンドボックスを親に紐づけ直せる。
                int? productionOrganizationId = null;
                foreach (var site in sites.Sites.OrderBy(s => s.IsSandbox))
                {
                    var provisioned = await _provisioning.ProvisionAsync(
                        site,
                        signupRequest.OrganizationName,
                        signupRequest.EcCubeVersion,
                        subject,
                        parentOrganizationId: site.IsSandbox ? productionOrganizationId : null,
                        ct);

                    if (!site.IsSandbox)
                    {
                        productionOrganizationId = provisioned.Organization.Id;
                    }
                }

                signupRequest.ConfirmedAt = DateTimeOffset.UtcNow;

                await _context.SaveChangesAsync(ct);

                // 初回パスキー登録を認可する一回限りトークンを同一トランザクションで発行する。
                // accounts コンソールは public client のため、登録 API はこのトークンで認可する。
                var registrationToken = await _registrationTokenService.IssueAsync(subject, ct);

                await transaction.CommitAsync(ct);

                _logger.LogInformation(
                    "申込を確認し本登録が完了しました: Tenant={Tenant}, TokenHash={TokenHash}, Subject={Subject}, Orgs={OrgCount}",
                    signupRequest.TenantName, TokenHashPrefix(token), subject, sites.Sites.Count);

                return new ISignupService.ConfirmResult(signupRequest, registrationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // confirm 中に別リクエストが先に INSERT したことによるユニーク制約違反（TOCTOU）。
                // 事前チェック（メール既登録・組織コード重複）をすり抜けた真の競合のみがここに到達する。
                // 違反したインデックス名で分岐し、409 に正規化して適切なメッセージを返す。
                await transaction.RollbackAsync(ct);

                if (IsEmailUniqueViolation(ex))
                {
                    // Account.(OrganizationId, Email) または B2BUser.(OrganizationId, ExternalId) の競合。
                    // 同一メールの再登録に該当するため、URL 変更では解決しない旨が伝わるエラーを返す。
                    _logger.LogWarning(ex,
                        "申込確認中にメールアドレスのユニーク制約違反が発生しました（競合）: Tenant={Tenant}, TokenHash={TokenHash}",
                        signupRequest.TenantName, TokenHashPrefix(token));
                    throw new SignupValidationException(
                        "email_already_registered",
                        "このメールアドレスは既に登録されています。",
                        field: "email",
                        statusCode: 409);
                }

                // それ以外（組織コード・client_id・rsa kid 等）の制約違反は組織コード重複として扱う。
                _logger.LogWarning(ex,
                    "申込確認中に組織コードのユニーク制約違反が発生しました（競合）: Tenant={Tenant}, TokenHash={TokenHash}",
                    signupRequest.TenantName, TokenHashPrefix(token));
                throw new SignupValidationException(
                    "organization_already_exists",
                    "このドメインは既に EcAuth に登録されています。別のサイト URL でお申し込みください。",
                    field: "production_site_url",
                    statusCode: 409);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
            }
        }

        /// <summary>
        /// <see cref="DbUpdateException"/> が SQL Server のユニーク／主キー制約違反
        /// （エラー番号 2601 / 2627）に起因するかを判定する。
        /// </summary>
        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            return ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx
                && (sqlEx.Number == 2601 || sqlEx.Number == 2627);
        }

        // Account.(OrganizationId, Email) / B2BUser.(OrganizationId, ExternalId) のユニークインデックス名。
        // SQL Server のユニーク制約違反メッセージ（エラー 2601/2627）には違反したインデックス名が含まれる。
        private const string AccountEmailIndexName = "IX_account_organization_id_email";
        private const string B2BUserExternalIdIndexName = "IX_b2b_user_organization_id_external_id";

        /// <summary>
        /// ユニーク制約違反が Account のメール／B2BUser の external_id インデックスに起因するか
        /// （= 同一メールの再登録に相当するか）を、InnerException のメッセージに含まれる
        /// インデックス名で判定する。判定は大文字小文字を無視する。
        /// </summary>
        private static bool IsEmailUniqueViolation(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message;
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            return message.Contains(AccountEmailIndexName, StringComparison.OrdinalIgnoreCase)
                || message.Contains(B2BUserExternalIdIndexName, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public async Task<SignupStatus> GetStatusAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return SignupStatus.NotFound;
            }

            using (TimingScope.Begin("status_lookup"))
            {
                // 受信した生トークンをハッシュ化して照合する。テナント絞り込みは
                // グローバルクエリフィルター（TenantName）に委ねる。
                var tokenHash = HashConfirmToken(token);
                var signupRequest = await _context.SignupRequests
                    .FirstOrDefaultAsync(sr => sr.ConfirmTokenHash == tokenHash, ct);

                if (signupRequest == null)
                {
                    return SignupStatus.NotFound;
                }

                if (signupRequest.ConfirmedAt != null)
                {
                    return SignupStatus.Confirmed;
                }

                if (signupRequest.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    return SignupStatus.Expired;
                }

                return SignupStatus.Pending;
            }
        }

        // ---- バリデーション ----

        private string ValidateAndNormalizeEmail(string? rawEmail)
        {
            var email = rawEmail?.Trim().ToLowerInvariant() ?? string.Empty;

            // Email カラムは nvarchar(255)。255 超は SaveChanges で 500 になるため、ここで 422 として弾く。
            if (string.IsNullOrEmpty(email) || email.Length > 255 || !IsValidEmail(email))
            {
                throw new SignupValidationException(
                    "invalid_email", "メールアドレスの形式が正しくありません。", field: "email");
            }

            if (_disposableEmailChecker.IsDisposable(email))
            {
                throw new SignupValidationException(
                    "disposable_email",
                    "使い捨てメールアドレスはご利用いただけません。常用のメールアドレスでお申し込みください。",
                    field: "email");
            }

            return email;
        }

        private static string ValidateOrganizationName(string? rawName)
        {
            var name = rawName?.Trim() ?? string.Empty;

            if (name.Length < 1 || name.Length > 100)
            {
                throw new SignupValidationException(
                    "invalid_organization_name", "組織名は 1〜100 文字で入力してください。", field: "organization_name");
            }

            return name;
        }

        private SiteSet ValidateSiteUrls(string? productionSiteUrl, string? testSiteUrl)
        {
            var production = NormalizeOptionalUrl(productionSiteUrl);
            var test = NormalizeOptionalUrl(testSiteUrl);

            // 本番サイトは必須。テストサイトだけの申込を許すと、紐づく本番が無い
            // サンドボックス Org（parent_organization_id が null）ができてしまう。
            // このサンドボックスは「1 本番あたりテストは 1 件」の判定
            // （AccountController が ParentOrganizationId で数える）に引っかからないため、
            // 後から本番を追加するとサンドボックスが 2 件並ぶ状態を作れてしまう。
            // テスト環境から始めたい利用者には、本番ドメインを申込時に決めてもらったうえで
            // テストサイトを併記する運用に寄せる。
            if (production == null)
            {
                throw new SignupValidationException(
                    "invalid_site_url",
                    "本番サイト URL を入力してください。",
                    field: "production_site_url");
            }

            var sites = new List<SiteEntry>();

            if (production != null)
            {
                sites.Add(_provisioning.BuildSite(production, isSandbox: false, "production_site_url"));
            }

            if (test != null)
            {
                sites.Add(_provisioning.BuildSite(test, isSandbox: true, "test_site_url"));
            }

            return new SiteSet(
                ProductionUrl: production,
                TestUrl: test,
                Sites: sites);
        }

        private static void ValidateEcCubeVersion(string? version)
        {
            var v = version?.Trim();
            if (v != "2" && v != "4" && v != "other")
            {
                throw new SignupValidationException(
                    "unsupported_version", "対応していない EC-CUBE バージョンです。", field: "ec_cube_version");
            }
        }

        private static string? NormalizeOptionalUrl(string? url)
        {
            var trimmed = url?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        private static bool IsValidEmail(string email)
        {
            try
            {
                // System.Net.Mail.MailAddress は RFC 5322 に概ね準拠した解析を行う。
                var addr = new System.Net.Mail.MailAddress(email);
                return string.Equals(addr.Address, email, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ---- トークン・URL・補助 ----

        private static string GenerateConfirmToken()
        {
            // 32 byte の URL-safe ランダム。Base64URL（パディング除去）でエンコードする。
            var bytes = RandomNumberGenerator.GetBytes(ConfirmTokenBytes);
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
        /// 確認トークンを SHA-256 でハッシュ化し、16 進小文字（64 文字）で返す。
        /// トークンは 256bit の高エントロピーなランダム値のためソルトは不要。
        /// 生トークンはメール URL にのみ使用し、DB にはこのハッシュのみを保存する。
        /// </summary>
        private static string HashConfirmToken(string token)
        {
            return Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
                .ToLowerInvariant();
        }

        /// <summary>
        /// ログ出力用に平文トークンの SHA-256 ハッシュ先頭 8 文字を返す（平文は出力しない）。
        /// </summary>
        private static string TokenHashPrefix(string token)
        {
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        private static string NormalizePolicyVersion(string? version)
        {
            var v = version?.Trim();
            return string.IsNullOrEmpty(v) ? DefaultPolicyVersion : v;
        }

        /// <summary>
        /// 確認 URL を組み立てる。基底 URL はテナント別の設定値
        /// <c>Signup:ConfirmBaseUrl:{tenant_name}</c>（例: <c>Signup:ConfirmBaseUrl:accounts</c>）からのみ取得する。
        /// <para>
        /// テナント名にハイフンを含む場合（例: <c>stg-accounts</c>）、環境変数名にハイフンを使えない
        /// （Azure Linux App Service が拒否）ため、キーのテナント部は <c>[A-Za-z0-9_]</c> 以外を
        /// <c>_</c> に正規化する（<c>stg-accounts</c> → 参照キー <c>Signup:ConfirmBaseUrl:stg_accounts</c>、
        /// 環境変数 <c>Signup__ConfirmBaseUrl__stg_accounts</c>）。
        /// </para>
        /// <para>
        /// この設定値は「フロントエンドのベース URL」を指す。確認リンクはフロントエンド
        /// （<c>/signup/confirm</c>）を経由させ（Option B）、フロント側が JS で確認 API を
        /// 呼び出す前提とする。これによりメール内リンクの GET アクセスで副作用が発生しない。
        /// </para>
        /// <para>
        /// Host ヘッダ偽装によるトークン窃取（フィッシング）を防ぐため、
        /// <c>HttpContext.Request.Host</c> へのフォールバックは行わない。設定が無い／不正テナントの場合は例外を投げて停止する。
        /// </para>
        /// </summary>
        private string BuildConfirmUrl(string confirmToken)
        {
            var encodedToken = Uri.EscapeDataString(confirmToken);

            // 確認 URL の基底はテナント別の信頼済み設定値（フロントエンドのベース URL）のみを使用する（Request.Host は信頼しない）。
            // 環境変数名にハイフンを使えないため、キーのテナント部を env-var-safe に正規化する（"stg-accounts" -> "stg_accounts"）。
            var tenantName = _tenantService.TenantName;
            var configKey = $"Signup:ConfirmBaseUrl:{NonConfigKeyChar.Replace(tenantName, "_")}";
            var configuredBase = _configuration[configKey];

            if (string.IsNullOrWhiteSpace(configuredBase)
                || !Uri.TryCreate(configuredBase, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError(
                    "確認 URL の基底が未設定または不正です: Tenant={Tenant}, Key={Key}",
                    tenantName, configKey);
                throw new InvalidOperationException(
                    $"確認 URL の基底を決定できません。{configKey} に有効な https:// URL を設定してください。");
            }

            // Option B: フロントエンド経由の確認ページ（/signup/confirm）に遷移させる。
            return $"{configuredBase.TrimEnd('/')}/signup/confirm?token={encodedToken}";
        }

        // ---- 内部表現 ----

        /// <summary>
        /// 申込 1 件が作るサイトの集合。<c>Sites</c> の各要素は
        /// <see cref="IOrganizationProvisioningService.BuildSite"/> が検証・導出した結果。
        /// </summary>
        private sealed record SiteSet(string? ProductionUrl, string? TestUrl, List<SiteEntry> Sites);
    }
}
