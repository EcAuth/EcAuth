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

        // 組織コード導出: 英数字以外の連続を 1 つの "-" に畳み込むための正規表現。
        private static readonly Regex NonAlphanumericRun = new("[^a-z0-9]+", RegexOptions.Compiled);

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
        private readonly ISecretProtector _secretProtector;
        private readonly IPasskeyRegistrationTokenService _registrationTokenService;

        public SignupService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IEmailService emailService,
            IDisposableEmailChecker disposableEmailChecker,
            IConfiguration configuration,
            ILogger<SignupService> logger,
            ISecretProtector secretProtector,
            IPasskeyRegistrationTokenService registrationTokenService)
        {
            _context = context;
            _tenantService = tenantService;
            _emailService = emailService;
            _disposableEmailChecker = disposableEmailChecker;
            _configuration = configuration;
            _logger = logger;
            _secretProtector = secretProtector;
            _registrationTokenService = registrationTokenService;
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
                await EnsureOrganizationCodesAvailableAsync(sites, ct);
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

                // 申込時から confirm までの間にデータが変わっている可能性があるため、再バリデーションする。
                sites = ValidateSiteUrls(signupRequest.ProductionSiteUrl, signupRequest.TestSiteUrl);
                // confirm 時の code 衝突は Race Condition のため 409 を返す。
                await EnsureOrganizationCodesAvailableAsync(sites, ct, statusCode: 409);

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

                // 顧客 Organization を入力 URL に応じて 1〜2 件作成し、
                // 各 Org に Client / RsaKeyPair / AccountOrganization を作成する。
                foreach (var site in sites.Sites)
                {
                    var organization = new Organization
                    {
                        Code = site.Code,
                        Name = signupRequest.OrganizationName,
                        TenantName = site.Code,
                        IsSandbox = site.IsSandbox
                    };
                    _context.Organizations.Add(organization);
                    // RsaKeyPair / AccountOrganization が OrganizationId を必要とするため、
                    // ここで一度 SaveChanges して採番された Id を確定させる。
                    await _context.SaveChangesAsync(ct);

                    var client = CreateClient(organization, site, signupRequest.OrganizationName);
                    // 保存前に client_secret を暗号化する（レガシー/dev は平文パススルー）。
                    // Key Vault 暗号化の所要時間を confirm 内の独立ステップとして計測する。
                    using (TimingScope.Begin("client_secret_protect"))
                    {
                        client.ClientSecret = await _secretProtector.ProtectAsync(client.ClientSecret, ct);
                    }
                    _context.Clients.Add(client);

                    _context.RsaKeyPairs.Add(CreateRsaKeyPair(organization.Id));

                    _context.AccountOrganizations.Add(new AccountOrganization
                    {
                        AccountSubject = subject,
                        OrganizationId = organization.Id,
                        Role = "owner"
                    });
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

        private static SiteSet ValidateSiteUrls(string? productionSiteUrl, string? testSiteUrl)
        {
            var production = NormalizeOptionalUrl(productionSiteUrl);
            var test = NormalizeOptionalUrl(testSiteUrl);

            if (production == null && test == null)
            {
                throw new SignupValidationException(
                    "invalid_site_url",
                    "本番サイト URL またはテストサイト URL のいずれかを入力してください。",
                    field: "production_site_url");
            }

            string? productionHost = null;
            string? testHost = null;

            if (production != null)
            {
                productionHost = ValidateHttpsAndGetHost(production, "production_site_url");
            }
            if (test != null)
            {
                testHost = ValidateHttpsAndGetHost(test, "test_site_url");
            }

            var sites = new List<SiteEntry>();

            string? productionCode = null;
            if (productionHost != null)
            {
                productionCode = DeriveOrganizationCode(productionHost);
                sites.Add(new SiteEntry(productionCode, productionHost, IsSandbox: false, "production_site_url"));
            }

            // テスト Org は、本番がない場合か、本番と「導出後の組織コード」が異なる場合のみ作成する。
            // ホスト名は異なっても導出後コードが同一になるケース（例: www.shop.example.jp と
            // shop.example.jp はいずれも shop-example-jp）があるため、生ホスト名ではなく
            // 導出後コードで比較し、コード重複によるユニーク制約違反を防ぐ。
            if (testHost != null)
            {
                var testCode = DeriveOrganizationCode(testHost);
                if (productionCode == null
                    || !string.Equals(testCode, productionCode, StringComparison.OrdinalIgnoreCase))
                {
                    sites.Add(new SiteEntry(testCode, testHost, IsSandbox: true, "test_site_url"));
                }
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

        private async Task EnsureOrganizationCodesAvailableAsync(SiteSet sites, CancellationToken ct, int statusCode = 422)
        {
            // 同一リクエスト内で導出後の組織コードが衝突していないか検知する
            // （本番・テストの URL が異なっても同じコードに導出されるケースを 422 で弾く）。
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in sites.Sites)
            {
                if (!seenCodes.Add(site.Code))
                {
                    throw new SignupValidationException(
                        "duplicate_site",
                        "本番サイトとテストサイトが同一の組織コードに導出されます。異なるドメインでお申し込みください。",
                        field: site.Field,
                        statusCode: 422);
                }
            }

            foreach (var site in sites.Sites)
            {
                var exists = await _context.Organizations
                    .IgnoreQueryFilters()
                    .AnyAsync(o => o.Code == site.Code, ct);

                if (exists)
                {
                    throw new SignupValidationException(
                        "organization_already_exists",
                        "このドメインは既に EcAuth に登録されています。別のサイト URL でお申し込みください。",
                        field: site.Field,
                        statusCode: statusCode);
                }
            }
        }

        // ---- 組織コード導出・URL 処理 ----

        /// <summary>
        /// ホスト名から組織コードを導出する。
        /// lowercase → 先頭 www. 除去 → サブドメイン保持 → 英数以外の連続を "-" に置換 → 前後の "-" を trim。
        /// 例: <c>shop.example.jp → shop-example-jp</c>。
        /// </summary>
        private static string DeriveOrganizationCode(string host)
        {
            var normalized = host.Trim().ToLowerInvariant();
            if (normalized.StartsWith("www.", StringComparison.Ordinal))
            {
                normalized = normalized["www.".Length..];
            }

            var code = NonAlphanumericRun.Replace(normalized, "-").Trim('-');
            return code;
        }

        private static string? NormalizeOptionalUrl(string? url)
        {
            var trimmed = url?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        /// <summary>
        /// URL が HTTPS であることを検証し、ホスト名を返す。
        /// </summary>
        private static string ValidateHttpsAndGetHost(string url, string field)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(uri.Host))
            {
                throw new SignupValidationException(
                    "invalid_site_url", "サイト URL は https:// で始まる正しい URL を入力してください。", field: field);
            }

            // IDN（国際化ドメイン）は Uri.Host だと Unicode のまま返り、組織コード導出の
            // [^a-z0-9] 除去で空文字や衝突を招く。IdnHost（Punycode, ASCII）を使う。
            return uri.IdnHost;
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

        // ---- レコード生成（AccountsOrganizationSeeder の流儀を流用）----

        /// <summary>
        /// 顧客 Org 用 Client を生成する。ClientSecret 生成・AllowedRpIds 設定・RedirectUri 付与の流儀は
        /// <c>AccountsOrganizationSeeder.SeedClientAsync</c> / <c>SeedRedirectUriAsync</c> を流用する。
        /// </summary>
        private static Client CreateClient(Organization organization, SiteEntry site, string appName)
        {
            var client = new Client
            {
                ClientId = BuildClientId(site.Code),
                ClientSecret = GenerateClientSecret(),
                AppName = appName,
                OrganizationId = organization.Id,
                SubjectType = SubjectType.B2B,
                AllowedRpIds = new List<string> { site.Host }
            };

            // RedirectUri は申込時には不明のため、サイトのトップ URL を暫定登録する。
            client.RedirectUris!.Add(new RedirectUri
            {
                Uri = $"https://{site.Host}/"
            });

            return client;
        }

        /// <summary>
        /// RSA 鍵ペアを生成する。RSA.Create(2048) → Base64 エクスポートの流儀は
        /// <c>AccountsOrganizationSeeder.SeedRsaKeyPairAsync</c> を流用する。
        /// </summary>
        private static RsaKeyPair CreateRsaKeyPair(int organizationId)
        {
            using var rsa = RSA.Create(2048);
            return new RsaKeyPair
            {
                Kid = Guid.NewGuid().ToString(),
                OrganizationId = organizationId,
                PublicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey()),
                PrivateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey()),
                IsActive = true
            };
        }

        // ---- トークン・URL・補助 ----

        private static string GenerateConfirmToken()
        {
            // 32 byte の URL-safe ランダム。Base64URL（パディング除去）でエンコードする。
            var bytes = RandomNumberGenerator.GetBytes(ConfirmTokenBytes);
            return Base64UrlEncode(bytes);
        }

        private static string GenerateClientSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncode(bytes);
        }

        /// <summary>
        /// 顧客 Org 用の client_id を組織コードから導出する。
        /// グローバルユニーク制約があるため、組織コードに短いランダムサフィックスを付与して衝突を避ける。
        /// </summary>
        private static string BuildClientId(string code)
        {
            return $"ec-{code}-{Guid.NewGuid():N}";
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

        private sealed record SiteSet(string? ProductionUrl, string? TestUrl, List<SiteEntry> Sites);

        private sealed record SiteEntry(string Code, string Host, bool IsSandbox, string Field);
    }
}
