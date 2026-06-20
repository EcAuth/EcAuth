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
    /// <summary>
    /// マジックリンクログイン（Phase D-2）のサービス層テスト。
    /// <para>
    /// 並行下のアトミック性（Compare-And-Set）は SQL Server の単一 UPDATE 文に依存し、実 DB（統合 / E2E）で
    /// 担保する。<c>ExecuteUpdate</c> は InMemory プロバイダー非対応のため、ここでは
    /// <see cref="TestableMagicLinkService"/> で消費シーム（<c>TryConsumeTokenAsync</c>）を逐次の
    /// read-check-update に差し替え、逐次の単発契約・期限切れ拒否・レート制限境界・Email enumeration 対策・
    /// 認可コード発行の配線を InMemory で検証する。
    /// </para>
    /// </summary>
    public class MagicLinkServiceTests
    {
        private const string Tenant = "accounts";
        private const string FrontBaseUrl = "https://ec-auth.io";
        private const int AccountsOrgId = 1;
        private const string AccountSubject = "acc-subject-0001";
        private const string AccountEmail = "owner@example.jp";
        private const string AdminClientId = "ecauth-admin-console";
        private const string AdminRedirectUri = "https://accounts.ec-auth.io/auth/callback";

        private readonly ILogger<MagicLinkService> _logger =
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MagicLinkService>();

        /// <summary>
        /// 消費シームを InMemory でも動く逐次 read-check-update に差し替えたテスト用サービス。
        /// 本番（<see cref="MagicLinkService"/>）は <c>ExecuteUpdate</c> によるアトミック UPDATE を使う。
        /// </summary>
        private sealed class TestableMagicLinkService : MagicLinkService
        {
            private readonly EcAuthDbContext _ctx;

            public TestableMagicLinkService(
                EcAuthDbContext ctx,
                ITenantService tenantService,
                IAccountService accountService,
                IAuthorizationCodeService authorizationCodeService,
                IEmailService emailService,
                IConfiguration configuration,
                ILogger<MagicLinkService> logger)
                : base(ctx, tenantService, accountService, authorizationCodeService, emailService, configuration, logger)
            {
                _ctx = ctx;
            }

            protected override async Task<int> TryConsumeTokenAsync(
                string tokenHash, DateTimeOffset now, CancellationToken ct)
            {
                var token = await _ctx.MagicLoginTokens
                    .FirstOrDefaultAsync(
                        t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now, ct);
                if (token == null)
                {
                    return 0;
                }
                token.UsedAt = now;
                await _ctx.SaveChangesAsync(ct);
                return 1;
            }
        }

        // ---- セットアップ ----

        private static MockTenantService CreateTenantService(string tenant = Tenant)
        {
            var t = new MockTenantService();
            t.SetTenant(tenant);
            return t;
        }

        private static IConfiguration CreateConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"MagicLink:BaseUrl:{Tenant}"] = FrontBaseUrl
                })
                .Build();

        private static Organization SeedAccountsOrg(EcAuthDbContext context)
        {
            var org = new Organization
            {
                Id = AccountsOrgId,
                Code = Tenant,
                Name = "EcAuth Accounts",
                TenantName = Tenant
            };
            context.Organizations.Add(org);
            context.SaveChanges();
            return org;
        }

        private static Account SeedAccount(EcAuthDbContext context)
        {
            var account = new Account
            {
                Subject = AccountSubject,
                Email = AccountEmail,
                OrganizationId = AccountsOrgId,
                EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            context.Accounts.Add(account);
            context.SaveChanges();
            return account;
        }

        private static Client SeedAdminConsoleClient(EcAuthDbContext context)
        {
            var client = new Client
            {
                ClientId = AdminClientId,
                ClientSecret = "admin-console-secret",
                AppName = "EcAuth Accounts",
                OrganizationId = AccountsOrgId,
                SubjectType = SubjectType.Account
            };
            client.RedirectUris!.Add(new RedirectUri { Uri = AdminRedirectUri });
            context.Clients.Add(client);
            context.SaveChanges();
            return client;
        }

        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        private TestableMagicLinkService CreateService(
            EcAuthDbContext context,
            ITenantService tenantService,
            out Mock<IEmailService> emailMock,
            out Mock<IAccountService> accountMock,
            out Mock<IAuthorizationCodeService> authCodeMock)
        {
            emailMock = new Mock<IEmailService>();
            accountMock = new Mock<IAccountService>();
            authCodeMock = new Mock<IAuthorizationCodeService>();

            return new TestableMagicLinkService(
                context,
                tenantService,
                accountMock.Object,
                authCodeMock.Object,
                emailMock.Object,
                CreateConfiguration(),
                _logger);
        }

        // ---- RequestAsync ----

        [Fact]
        public async Task RequestAsync_ExistingAccount_SendsMagicLinkAndPersistsToken()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            SeedAccount(context);

            var service = CreateService(context, tenant, out var emailMock, out _, out _);

            string? sentUrl = null;
            emailMock
                .Setup(e => e.SendMagicLoginLinkAsync(AccountEmail, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, url, _) => sentUrl = url)
                .Returns(Task.CompletedTask);

            await service.RequestAsync(AccountEmail, "203.0.113.1", "UnitTest/1.0");

            emailMock.Verify(
                e => e.SendMagicLoginLinkAsync(AccountEmail, It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
            Assert.NotNull(sentUrl);
            Assert.StartsWith($"{FrontBaseUrl}/signin/magic-link?token=", sentUrl);

            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.Equal(AccountSubject, token.AccountSubject);
            Assert.False(string.IsNullOrEmpty(token.TokenHash));
            // 生トークンは DB に保存されない（URL のトークンと token_hash が一致せず、ハッシュ化後に一致する）。
            var rawToken = Uri.UnescapeDataString(sentUrl!.Split("token=")[1]);
            Assert.NotEqual(rawToken, token.TokenHash);
            Assert.Equal(HashToken(rawToken), token.TokenHash);
        }

        [Fact]
        public async Task RequestAsync_UnknownEmail_DoesNotSendEmailButRecordsForRateLimit()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);

            var service = CreateService(context, tenant, out var emailMock, out _, out _);

            await service.RequestAsync("nobody@example.jp", "203.0.113.2", "UnitTest/1.0");

            emailMock.Verify(
                e => e.SendMagicLoginLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // レート制限の母数として行は記録され、AccountSubject は null。
            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.Null(token.AccountSubject);
        }

        [Fact]
        public async Task RequestAsync_InvalidEmail_NoThrowNoPersistNoEmail()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);

            var service = CreateService(context, tenant, out var emailMock, out _, out _);

            // 形式不正は例外を投げず正常終了（enumeration を漏らさない）。記録もメールもしない。
            await service.RequestAsync("not-an-email", "203.0.113.3", "UnitTest/1.0");

            emailMock.Verify(
                e => e.SendMagicLoginLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(0, await context.MagicLoginTokens.CountAsync());
        }

        [Fact]
        public async Task RequestAsync_SecondRequestWithinFiveMinutes_ThrowsRateLimited()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            SeedAccount(context);

            var service = CreateService(context, tenant, out var emailMock, out _, out _);
            emailMock
                .Setup(e => e.SendMagicLoginLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await service.RequestAsync(AccountEmail, "203.0.113.4", "UnitTest/1.0");

            var ex = await Assert.ThrowsAsync<MagicLinkException>(
                () => service.RequestAsync(AccountEmail, "203.0.113.4", "UnitTest/1.0"));
            Assert.Equal(429, ex.StatusCode);
            Assert.Equal("rate_limited", ex.Error);
        }

        [Fact]
        public async Task RequestAsync_EleventhRequestFromSameIp_ThrowsRateLimited()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);

            var service = CreateService(context, tenant, out _, out _, out _);

            const string ip = "203.0.113.50";
            // 異なるメール（存在しない）で IP のみ共通にし、メール 5 分制限を回避する。
            for (var i = 0; i < 10; i++)
            {
                await service.RequestAsync($"user{i}@example.jp", ip, "UnitTest/1.0");
            }

            var ex = await Assert.ThrowsAsync<MagicLinkException>(
                () => service.RequestAsync("user-overflow@example.jp", ip, "UnitTest/1.0"));
            Assert.Equal(429, ex.StatusCode);
        }

        // ---- VerifyAsync ----

        private static async Task<string> InsertMagicTokenAsync(
            EcAuthDbContext context, DateTimeOffset expiresAt, string? accountSubject = AccountSubject)
        {
            var rawToken = $"raw-token-{Guid.NewGuid():N}";
            context.MagicLoginTokens.Add(new MagicLoginToken
            {
                AccountSubject = accountSubject,
                RequestedEmailHash = HashToken(AccountEmail),
                TokenHash = HashToken(rawToken),
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            return rawToken;
        }

        [Fact]
        public async Task VerifyAsync_ValidToken_ConsumesAndReturnsRedirectWithCode()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            var account = SeedAccount(context);
            var client = SeedAdminConsoleClient(context);

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var service = CreateService(context, tenant, out _, out var accountMock, out var authCodeMock);
            accountMock.Setup(a => a.GetBySubjectAsync(AccountSubject)).ReturnsAsync(account);

            IAuthorizationCodeService.AuthorizationCodeRequest? captured = null;
            authCodeMock
                .Setup(a => a.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()))
                .Callback<IAuthorizationCodeService.AuthorizationCodeRequest>(r => captured = r)
                .ReturnsAsync(new AuthorizationCode { Code = "authcode-xyz" });

            var result = await service.VerifyAsync(rawToken);

            Assert.Equal($"{AdminRedirectUri}?code=authcode-xyz", result.RedirectUri);

            Assert.NotNull(captured);
            Assert.Equal(AccountSubject, captured!.Subject);
            Assert.Equal(SubjectType.Account, captured.SubjectType);
            Assert.Equal(client.Id, captured.ClientId);
            Assert.Equal(AdminRedirectUri, captured.RedirectUri);

            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.NotNull(token.UsedAt);
        }

        [Fact]
        public async Task VerifyAsync_SecondUse_ThrowsInvalidAndIssuesCodeOnlyOnce()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            var account = SeedAccount(context);
            SeedAdminConsoleClient(context);

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var service = CreateService(context, tenant, out _, out var accountMock, out var authCodeMock);
            accountMock.Setup(a => a.GetBySubjectAsync(AccountSubject)).ReturnsAsync(account);
            authCodeMock
                .Setup(a => a.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()))
                .ReturnsAsync(new AuthorizationCode { Code = "authcode-xyz" });

            await service.VerifyAsync(rawToken);

            // 2 回目は消費できず invalid（逐次の単発契約）。
            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            Assert.Equal("invalid_token", ex.Error);

            authCodeMock.Verify(
                a => a.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()),
                Times.Once);
        }

        [Fact]
        public async Task VerifyAsync_ExpiredToken_ThrowsInvalid()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            SeedAccount(context);
            SeedAdminConsoleClient(context);

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(-1));

            var service = CreateService(context, tenant, out _, out _, out var authCodeMock);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            authCodeMock.Verify(
                a => a.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()),
                Times.Never);
        }

        [Fact]
        public async Task VerifyAsync_UnknownToken_ThrowsInvalid()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);

            var service = CreateService(context, tenant, out _, out _, out _);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync("does-not-exist"));
            Assert.Equal(400, ex.StatusCode);
        }

        [Fact]
        public async Task VerifyAsync_EmptyToken_ThrowsInvalid()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);

            var service = CreateService(context, tenant, out _, out _, out _);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(""));
            Assert.Equal(400, ex.StatusCode);
        }

        [Fact]
        public async Task VerifyAsync_CrossTenantAccountNotFound_ThrowsInvalid()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            SeedAccount(context);
            SeedAdminConsoleClient(context);

            // トークン行は実在するが、別テナントのホストで使われた等の理由で GetBySubjectAsync が
            // null を返すケースを再現する（テナントクエリフィルターによる拒否）。
            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var service = CreateService(context, tenant, out _, out var accountMock, out var authCodeMock);
            accountMock.Setup(a => a.GetBySubjectAsync(It.IsAny<string>())).ReturnsAsync((Account?)null);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            authCodeMock.Verify(
                a => a.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()),
                Times.Never);
        }
    }
}
