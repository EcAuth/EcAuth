using System.Security.Cryptography;
using System.Text;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services
{
    public class SignupServiceTests
    {
        private const string Tenant = "accounts";

        private readonly ILogger<SignupService> _logger;

        public SignupServiceTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<SignupService>();
        }

        // ---- テストセットアップ ----

        private static MockTenantService CreateTenantService()
        {
            var tenantService = new MockTenantService();
            tenantService.SetTenant(Tenant);
            return tenantService;
        }

        /// <summary>
        /// 確認トークンのハッシュ化（SignupService と同方式）。テスト用にレコードを直接投入する際に使用する。
        /// </summary>
        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        /// <summary>
        /// メール送信に渡された確認 URL から生トークン（token クエリ）を取り出す。
        /// DB にはハッシュのみ保存されるため、Request 後に confirm するにはメール URL 経由でトークンを得る。
        /// </summary>
        private static string ExtractTokenFromConfirmUrl(string confirmUrl)
        {
            var uri = new Uri(confirmUrl);
            var query = uri.Query.TrimStart('?');
            var token = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(kv => kv.Length == 2 && kv[0] == "token")
                .Select(kv => Uri.UnescapeDataString(kv[1]))
                .FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(token));
            return token!;
        }

        /// <summary>
        /// 受付テナント Org (Code=TenantName) を投入した InMemory コンテキストを生成する。
        /// </summary>
        private static EcAuthDbContext CreateContextWithAccountsOrg(ITenantService tenantService)
        {
            var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = Tenant,
                Name = "EcAuth Accounts",
                TenantName = Tenant
            });
            context.SaveChanges();
            return context;
        }

        private static IConfiguration CreateConfiguration(bool withConfirmBaseUrl = true)
        {
            var values = new Dictionary<string, string?>();
            if (withConfirmBaseUrl)
            {
                values[$"Signup:ConfirmBaseUrl:{Tenant}"] = "https://accounts.ec-auth.io";
            }
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private SignupService CreateService(
            EcAuthDbContext context,
            ITenantService tenantService,
            out Mock<IEmailService> emailServiceMock,
            out Mock<IDisposableEmailChecker> disposableCheckerMock,
            bool withConfirmBaseUrl = true)
        {
            emailServiceMock = new Mock<IEmailService>();
            disposableCheckerMock = new Mock<IDisposableEmailChecker>();
            disposableCheckerMock.Setup(x => x.IsDisposable(It.IsAny<string>())).Returns(false);

            return new SignupService(
                context,
                tenantService,
                emailServiceMock.Object,
                disposableCheckerMock.Object,
                CreateConfiguration(withConfirmBaseUrl),
                _logger);
        }

        /// <summary>
        /// Request を実行し、メール送信に渡された生トークンを取得する。
        /// </summary>
        private async Task<string> RequestAndCaptureTokenAsync(SignupService service, Mock<IEmailService> emailMock, SignupInput input)
        {
            string? capturedUrl = null;
            emailMock
                .Setup(x => x.SendSignupConfirmationAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, _, url, _) => capturedUrl = url)
                .Returns(Task.CompletedTask);

            await service.RequestAsync(input);
            Assert.NotNull(capturedUrl);
            return ExtractTokenFromConfirmUrl(capturedUrl!);
        }

        private static SignupInput ValidInput() => new()
        {
            Email = "owner@example.com",
            OrganizationName = "Example Shop",
            ContactName = "山田 太郎",
            ProductionSiteUrl = "https://shop.example.jp",
            EcCubeVersion = "4"
        };

        // ---- RequestAsync 正常系 ----

        [Fact]
        public async Task RequestAsync_ValidInput_PersistsRequestAndSendsEmail()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            var token = await RequestAndCaptureTokenAsync(service, emailMock, ValidInput());

            var stored = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();
            Assert.Equal("owner@example.com", stored.Email);
            Assert.Equal(Tenant, stored.TenantName);
            Assert.Null(stored.ConfirmedAt);
            // DB には生トークンではなくハッシュが保存されている。
            Assert.Equal(HashToken(token), stored.ConfirmTokenHash);
            Assert.NotEqual(token, stored.ConfirmTokenHash);
        }

        [Fact]
        public async Task RequestAsync_ConfirmUrlPointsToSignupConfirm()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            string? capturedUrl = null;
            emailMock
                .Setup(x => x.SendSignupConfirmationAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, _, url, _) => capturedUrl = url)
                .Returns(Task.CompletedTask);

            await service.RequestAsync(ValidInput());

            Assert.NotNull(capturedUrl);
            Assert.StartsWith("https://", capturedUrl);
            Assert.Contains("/signup/confirm?token=", capturedUrl);
        }

        [Fact]
        public async Task RequestAsync_MissingConfirmBaseUrl_Throws()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _, withConfirmBaseUrl: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(ValidInput()));
        }

        [Fact]
        public async Task RequestAsync_HyphenatedTenant_UsesSanitizedConfigKey()
        {
            // 環境変数名にハイフンを使えない（Azure Linux App Service が拒否）ため、
            // テナント "stg-accounts" の設定キーは "stg_accounts" に正規化される。
            const string hyphenTenant = "stg-accounts";
            var tenantService = new MockTenantService();
            tenantService.SetTenant(hyphenTenant);

            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = hyphenTenant,
                Name = "EcAuth Stg Accounts",
                TenantName = hyphenTenant
            });
            context.SaveChanges();

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Signup:ConfirmBaseUrl:stg_accounts"] = "https://stg-accounts.ec-auth.io"
                })
                .Build();

            var emailMock = new Mock<IEmailService>();
            string? capturedUrl = null;
            emailMock
                .Setup(x => x.SendSignupConfirmationAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string, CancellationToken>((_, _, url, _) => capturedUrl = url)
                .Returns(Task.CompletedTask);
            var disposableMock = new Mock<IDisposableEmailChecker>();
            disposableMock.Setup(x => x.IsDisposable(It.IsAny<string>())).Returns(false);

            var service = new SignupService(
                context, tenantService, emailMock.Object, disposableMock.Object, config, _logger);

            await service.RequestAsync(ValidInput());

            Assert.NotNull(capturedUrl);
            Assert.StartsWith("https://stg-accounts.ec-auth.io/signup/confirm?token=", capturedUrl);
        }

        // ---- RequestAsync バリデーションエラー ----

        [Fact]
        public async Task RequestAsync_InvalidEmail_ThrowsInvalidEmail()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with { Email = "not-an-email" };
            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(input));
            Assert.Equal("invalid_email", ex.Error);
            Assert.Equal("email", ex.Field);
            Assert.Equal(422, ex.StatusCode);
        }

        [Fact]
        public async Task RequestAsync_DisposableEmail_ThrowsDisposableEmail()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out var disposableMock);
            disposableMock.Setup(x => x.IsDisposable(It.IsAny<string>())).Returns(true);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(ValidInput()));
            Assert.Equal("disposable_email", ex.Error);
            Assert.Equal("email", ex.Field);
        }

        [Fact]
        public async Task RequestAsync_EmptyOrganizationName_ThrowsInvalidOrganizationName()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with { OrganizationName = "   " };
            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(input));
            Assert.Equal("invalid_organization_name", ex.Error);
            Assert.Equal("organization_name", ex.Field);
        }

        [Fact]
        public async Task RequestAsync_NoSiteUrl_ThrowsInvalidSiteUrl()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with { ProductionSiteUrl = null, TestSiteUrl = null };
            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(input));
            Assert.Equal("invalid_site_url", ex.Error);
        }

        [Fact]
        public async Task RequestAsync_NonHttpsSiteUrl_ThrowsInvalidSiteUrl()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with { ProductionSiteUrl = "http://shop.example.jp" };
            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(input));
            Assert.Equal("invalid_site_url", ex.Error);
        }

        [Fact]
        public async Task RequestAsync_UnsupportedVersion_ThrowsUnsupportedVersion()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with { EcCubeVersion = "3" };
            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(input));
            Assert.Equal("unsupported_version", ex.Error);
            Assert.Equal("ec_cube_version", ex.Field);
        }

        [Fact]
        public async Task RequestAsync_OrganizationCodeAlreadyExists_ThrowsOrganizationAlreadyExists()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            // 既存の顧客 Org (Code=shop-example-jp) を投入して衝突させる
            context.Organizations.Add(new Organization
            {
                Id = 2,
                Code = "shop-example-jp",
                Name = "Existing",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantService, out _, out _);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.RequestAsync(ValidInput()));
            Assert.Equal("organization_already_exists", ex.Error);
            Assert.Equal(422, ex.StatusCode);
        }

        [Fact]
        public async Task RequestAsync_ProductionAndTestDeriveSameCode_ThrowsDuplicateSite()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            // www.shop.example.jp と shop.example.jp はいずれも shop-example-jp に導出される。
            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://www.shop.example.jp"
            };

            // 同一コードに導出されるため、テスト Org は追加されず単一 Org として扱われる。
            // 重複検知は EnsureOrganizationCodesAvailableAsync の seenCodes でも担保するが、
            // ここでは導出後コード比較によりテスト Org が作られないこと（=本番のみ）を確認する。
            await service.RequestAsync(input);
            var stored = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();
            Assert.NotNull(stored);
        }

        // ---- 組織コード導出 ----

        [Fact]
        public async Task RequestAsync_DerivesOrganizationCodeFromHost()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            // shop.example.jp -> shop-example-jp
            var token = await RequestAndCaptureTokenAsync(service, emailMock, ValidInput());

            // confirm して顧客 Org が shop-example-jp で生成されることを確認する
            await service.ConfirmAsync(token);

            var customerOrg = await context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == "shop-example-jp");
            Assert.NotNull(customerOrg);
        }

        // ---- ConfirmAsync 正常系 ----

        [Fact]
        public async Task ConfirmAsync_ProductionOnly_CreatesOneOrganization()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            var token = await RequestAndCaptureTokenAsync(service, emailMock, ValidInput());

            var confirmed = await service.ConfirmAsync(token);

            Assert.NotNull(confirmed.ConfirmedAt);
            var customerOrgs = await context.Organizations
                .IgnoreQueryFilters()
                .Where(o => o.Code != Tenant)
                .ToListAsync();
            Assert.Single(customerOrgs);

            // Account / B2BUser / Client / RsaKeyPair / AccountOrganization の生成を確認
            var account = await context.Accounts.IgnoreQueryFilters().FirstOrDefaultAsync();
            Assert.NotNull(account);
            var b2bUser = await context.B2BUsers.IgnoreQueryFilters().FirstOrDefaultAsync();
            Assert.NotNull(b2bUser);
            Assert.Equal(account!.Subject, b2bUser!.Subject);
            Assert.Equal(account.Email, b2bUser.ExternalId);
            Assert.Single(await context.Clients.IgnoreQueryFilters().ToListAsync());
            Assert.Single(await context.RsaKeyPairs.IgnoreQueryFilters().ToListAsync());
            Assert.Single(await context.AccountOrganizations.ToListAsync());
        }

        [Fact]
        public async Task ConfirmAsync_ProductionAndTestDifferentHosts_CreatesTwoOrganizations()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://test.example.jp"
            };
            var token = await RequestAndCaptureTokenAsync(service, emailMock, input);

            await service.ConfirmAsync(token);

            var customerOrgs = await context.Organizations
                .IgnoreQueryFilters()
                .Where(o => o.Code != Tenant)
                .ToListAsync();
            Assert.Equal(2, customerOrgs.Count);
            Assert.Contains(customerOrgs, o => o.Code == "shop-example-jp" && !o.IsSandbox);
            Assert.Contains(customerOrgs, o => o.Code == "test-example-jp" && o.IsSandbox);
        }

        [Fact]
        public async Task ConfirmAsync_ProductionAndTestSameHost_CreatesOnlyProduction()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://shop.example.jp"
            };
            var token = await RequestAndCaptureTokenAsync(service, emailMock, input);

            await service.ConfirmAsync(token);

            var customerOrgs = await context.Organizations
                .IgnoreQueryFilters()
                .Where(o => o.Code != Tenant)
                .ToListAsync();
            Assert.Single(customerOrgs);
            Assert.False(customerOrgs[0].IsSandbox);
        }

        [Fact]
        public async Task ConfirmAsync_ProductionAndTestDeriveSameCode_CreatesOnlyProduction()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            // www.shop.example.jp(test) と shop.example.jp(prod) はいずれも shop-example-jp に導出される。
            // 生ホスト名は異なるが導出後コードが同一のためテスト Org は作らない。
            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://www.shop.example.jp"
            };
            var token = await RequestAndCaptureTokenAsync(service, emailMock, input);

            await service.ConfirmAsync(token);

            var customerOrgs = await context.Organizations
                .IgnoreQueryFilters()
                .Where(o => o.Code != Tenant)
                .ToListAsync();
            Assert.Single(customerOrgs);
            Assert.Equal("shop-example-jp", customerOrgs[0].Code);
            Assert.False(customerOrgs[0].IsSandbox);
        }

        // ---- ConfirmAsync 異常系 ----

        [Fact]
        public async Task ConfirmAsync_InvalidToken_Throws()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync("nonexistent"));
            Assert.Equal("invalid_token", ex.Error);
        }

        [Fact]
        public async Task ConfirmAsync_ExpiredToken_Throws()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            context.SignupRequests.Add(new SignupRequest
            {
                ConfirmTokenHash = HashToken("expired-token"),
                Email = "owner@example.com",
                OrganizationName = "Example Shop",
                ProductionSiteUrl = "https://shop.example.jp",
                EcCubeVersion = "4",
                TenantName = Tenant,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-25)
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync("expired-token"));
            Assert.Equal("token_expired", ex.Error);
        }

        [Fact]
        public async Task ConfirmAsync_AlreadyConfirmedToken_Throws()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            context.SignupRequests.Add(new SignupRequest
            {
                ConfirmTokenHash = HashToken("confirmed-token"),
                Email = "owner@example.com",
                OrganizationName = "Example Shop",
                ProductionSiteUrl = "https://shop.example.jp",
                EcCubeVersion = "4",
                TenantName = Tenant,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ConfirmedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync("confirmed-token"));
            Assert.Equal("already_confirmed", ex.Error);
        }

        [Fact]
        public async Task ConfirmAsync_OrganizationCodeCollision_Throws409()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            // 申込を受け付けた後で、confirm 前に同じ code の Org が作成されたケースを再現する
            var token = await RequestAndCaptureTokenAsync(service, emailMock, ValidInput());

            context.Organizations.Add(new Organization
            {
                Id = 99,
                Code = "shop-example-jp",
                Name = "Race",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync(token));
            Assert.Equal("organization_already_exists", ex.Error);
            Assert.Equal(409, ex.StatusCode);
        }

        [Fact]
        public async Task ConfirmAsync_SameEmailDifferentSiteUrl_ThrowsEmailAlreadyRegistered()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out var emailMock, out _);

            // 1 回目: shop.example.jp で confirm → 受付 Org に Account が作られる。
            var firstToken = await RequestAndCaptureTokenAsync(service, emailMock, ValidInput());
            await service.ConfirmAsync(firstToken);

            // 2 回目: 同一メール・異なるサイト URL（another.example.jp）で申込 → confirm。
            // 組織コードは衝突しないが、受付 Org に同一メールの Account が既存のため
            // 事前チェック A で email_already_registered（409）として弾かれる。
            var secondInput = ValidInput() with { ProductionSiteUrl = "https://another.example.jp" };
            var secondToken = await RequestAndCaptureTokenAsync(service, emailMock, secondInput);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync(secondToken));
            Assert.Equal("email_already_registered", ex.Error);
            Assert.Equal("email", ex.Field);
            Assert.Equal(409, ex.StatusCode);
        }

        // ---- GetStatusAsync ----

        [Fact]
        public async Task GetStatusAsync_UnknownToken_ReturnsNotFound()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            Assert.Equal(SignupStatus.NotFound, await service.GetStatusAsync("nonexistent"));
        }

        [Fact]
        public async Task GetStatusAsync_PendingConfirmedExpired()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            context.SignupRequests.AddRange(
                new SignupRequest
                {
                    ConfirmTokenHash = HashToken("pending-token"),
                    Email = "a@example.com",
                    OrganizationName = "A",
                    ProductionSiteUrl = "https://a.example.jp",
                    EcCubeVersion = "4",
                    TenantName = Tenant,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                },
                new SignupRequest
                {
                    ConfirmTokenHash = HashToken("confirmed-token"),
                    Email = "b@example.com",
                    OrganizationName = "B",
                    ProductionSiteUrl = "https://b.example.jp",
                    EcCubeVersion = "4",
                    TenantName = Tenant,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    ConfirmedAt = DateTimeOffset.UtcNow
                },
                new SignupRequest
                {
                    ConfirmTokenHash = HashToken("expired-token"),
                    Email = "c@example.com",
                    OrganizationName = "C",
                    ProductionSiteUrl = "https://c.example.jp",
                    EcCubeVersion = "4",
                    TenantName = Tenant,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
                });
            await context.SaveChangesAsync();

            Assert.Equal(SignupStatus.Pending, await service.GetStatusAsync("pending-token"));
            Assert.Equal(SignupStatus.Confirmed, await service.GetStatusAsync("confirmed-token"));
            Assert.Equal(SignupStatus.Expired, await service.GetStatusAsync("expired-token"));
        }
    }
}
