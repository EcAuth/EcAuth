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

            var result = await service.RequestAsync(ValidInput());

            Assert.False(string.IsNullOrEmpty(result.ConfirmToken));
            Assert.Equal("owner@example.com", result.Email);
            Assert.Equal(Tenant, result.TenantName);
            Assert.Null(result.ConfirmedAt);

            var stored = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();
            Assert.Equal(result.ConfirmToken, stored.ConfirmToken);

            emailMock.Verify(
                x => x.SendSignupConfirmationAsync(
                    "owner@example.com",
                    "Example Shop",
                    It.Is<string>(u => u.Contains("/api/signup/confirm?token=")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RequestAsync_MissingConfirmBaseUrl_Throws()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _, withConfirmBaseUrl: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(ValidInput()));
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

        // ---- 組織コード導出 ----

        [Fact]
        public async Task RequestAsync_DerivesOrganizationCodeFromHost()
        {
            var tenantService = CreateTenantService();
            using var context = CreateContextWithAccountsOrg(tenantService);
            var service = CreateService(context, tenantService, out _, out _);

            // shop.example.jp -> shop-example-jp
            await service.RequestAsync(ValidInput());

            // confirm して顧客 Org が shop-example-jp で生成されることを確認する
            var sr = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();
            await service.ConfirmAsync(sr.ConfirmToken);

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
            var service = CreateService(context, tenantService, out _, out _);

            await service.RequestAsync(ValidInput());
            var sr = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();

            var confirmed = await service.ConfirmAsync(sr.ConfirmToken);

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
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://test.example.jp"
            };
            await service.RequestAsync(input);
            var sr = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();

            await service.ConfirmAsync(sr.ConfirmToken);

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
            var service = CreateService(context, tenantService, out _, out _);

            var input = ValidInput() with
            {
                ProductionSiteUrl = "https://shop.example.jp",
                TestSiteUrl = "https://shop.example.jp"
            };
            await service.RequestAsync(input);
            var sr = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();

            await service.ConfirmAsync(sr.ConfirmToken);

            var customerOrgs = await context.Organizations
                .IgnoreQueryFilters()
                .Where(o => o.Code != Tenant)
                .ToListAsync();
            Assert.Single(customerOrgs);
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
                ConfirmToken = "expired-token",
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
                ConfirmToken = "confirmed-token",
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
            var service = CreateService(context, tenantService, out _, out _);

            // 申込を受け付けた後で、confirm 前に同じ code の Org が作成されたケースを再現する
            await service.RequestAsync(ValidInput());
            var sr = await context.SignupRequests.IgnoreQueryFilters().FirstAsync();

            context.Organizations.Add(new Organization
            {
                Id = 99,
                Code = "shop-example-jp",
                Name = "Race",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<SignupValidationException>(() => service.ConfirmAsync(sr.ConfirmToken));
            Assert.Equal("organization_already_exists", ex.Error);
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
                    ConfirmToken = "pending-token",
                    Email = "a@example.com",
                    OrganizationName = "A",
                    ProductionSiteUrl = "https://a.example.jp",
                    EcCubeVersion = "4",
                    TenantName = Tenant,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                },
                new SignupRequest
                {
                    ConfirmToken = "confirmed-token",
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
                    ConfirmToken = "expired-token",
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
