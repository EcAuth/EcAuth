using System.Security.Cryptography;
using System.Text;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// トークン発行の配線を InMemory で検証する。
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
                ITokenService tokenService,
                IEmailService emailService,
                IConfiguration configuration,
                IOptions<MagicLinkOptions> options,
                ILogger<MagicLinkService> logger)
                : base(ctx, tenantService, accountService, tokenService, emailService, configuration, options, logger)
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
            out Mock<ITokenService> tokenMock)
        {
            emailMock = new Mock<IEmailService>();
            accountMock = new Mock<IAccountService>();
            tokenMock = new Mock<ITokenService>();

            // managed_orgs はトークン発行の必須入力。個別テストで上書きしない限り空一覧を返す。
            accountMock
                .Setup(a => a.GetManagedOrganizationsAsync(It.IsAny<string>()))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization>());

            // トークン発行の既定応答（発行内容を検証するテストは個別に上書きする）。
            tokenMock
                .Setup(t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()))
                .ReturnsAsync(new ITokenService.TokenResponse
                {
                    AccessToken = "access-token-xyz",
                    IdToken = "id-token-xyz",
                    ExpiresIn = 3600,
                    TokenType = "Bearer"
                });

            return new TestableMagicLinkService(
                context,
                tenantService,
                accountMock.Object,
                tokenMock.Object,
                emailMock.Object,
                CreateConfiguration(),
                Options.Create(new MagicLinkOptions()),
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

        [Fact]
        public async Task RequestAsync_EmailSendFailure_DoesNotThrowAndPersistsToken()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            SeedAccount(context);

            var service = CreateService(context, tenant, out var emailMock, out _, out _);
            // SendGrid 障害・API キー未設定相当（InvalidOperationException）。
            emailMock
                .Setup(e => e.SendMagicLoginLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("SendGrid unavailable"));

            // 送信失敗でも例外を伝播させない（登録済みのみ 500・未登録 200 という enumeration を防ぐ）。
            await service.RequestAsync(AccountEmail, "203.0.113.9", "UnitTest/1.0");

            // トークンは記録されている（リクエスト自体は受理）。
            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.Equal(AccountSubject, token.AccountSubject);
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
        public async Task VerifyAsync_ValidToken_ConsumesAndReturnsTokensDirectly()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            var account = SeedAccount(context);
            var client = SeedAdminConsoleClient(context);

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var managedOrgs = new List<IAccountService.ManagedOrganization>
            {
                new(42, "shop-a", "owner")
            };

            var service = CreateService(context, tenant, out _, out var accountMock, out var tokenMock);
            accountMock.Setup(a => a.GetBySubjectAsync(AccountSubject)).ReturnsAsync(account);
            accountMock.Setup(a => a.GetManagedOrganizationsAsync(AccountSubject)).ReturnsAsync(managedOrgs);

            ITokenService.TokenRequest? captured = null;
            tokenMock
                .Setup(t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()))
                .Callback<ITokenService.TokenRequest>(r => captured = r)
                .ReturnsAsync(new ITokenService.TokenResponse
                {
                    AccessToken = "access-token-xyz",
                    IdToken = "id-token-xyz",
                    ExpiresIn = 3600,
                    TokenType = "Bearer"
                });

            var result = await service.VerifyAsync(rawToken);

            // 認可コードを介さず、トークンをそのまま返す（public client の PKCE 必須と両立させるため）。
            Assert.Equal("access-token-xyz", result.AccessToken);
            Assert.Equal("id-token-xyz", result.IdToken);
            Assert.Equal(3600, result.ExpiresIn);
            Assert.Equal("Bearer", result.TokenType);

            Assert.NotNull(captured);
            Assert.Equal(SubjectType.Account, captured!.SubjectType);
            Assert.Equal(AccountSubject, captured.User.Subject);
            Assert.Equal(client.Id, captured.Client.Id);
            // マイページが Client 一覧を引くため managed_orgs の伝播は必須。
            Assert.Equal(managedOrgs, captured.ManagedOrgs);

            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.NotNull(token.UsedAt);
        }

        [Fact]
        public async Task VerifyAsync_SecondUse_ThrowsInvalidAndIssuesTokensOnlyOnce()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            var account = SeedAccount(context);
            SeedAdminConsoleClient(context);

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var service = CreateService(context, tenant, out _, out var accountMock, out var tokenMock);
            accountMock.Setup(a => a.GetBySubjectAsync(AccountSubject)).ReturnsAsync(account);
            await service.VerifyAsync(rawToken);

            // 2 回目は消費できず invalid（逐次の単発契約）。
            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            Assert.Equal("invalid_token", ex.Error);

            tokenMock.Verify(
                t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()),
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

            var service = CreateService(context, tenant, out _, out _, out var tokenMock);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            tokenMock.Verify(
                t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()),
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

            var service = CreateService(context, tenant, out _, out var accountMock, out var tokenMock);
            accountMock.Setup(a => a.GetBySubjectAsync(It.IsAny<string>())).ReturnsAsync((Account?)null);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(400, ex.StatusCode);
            tokenMock.Verify(
                t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()),
                Times.Never);
        }

        [Fact]
        public async Task VerifyAsync_LoginNotConfigured_DoesNotConsumeToken()
        {
            var tenant = CreateTenantService();
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenant);
            SeedAccountsOrg(context);
            var account = SeedAccount(context);
            // 管理コンソール Client を投入しない → ResolveAccountClientAsync が login_not_configured(500)。

            var rawToken = await InsertMagicTokenAsync(context, DateTimeOffset.UtcNow.AddMinutes(10));

            var service = CreateService(context, tenant, out _, out var accountMock, out var tokenMock);
            accountMock.Setup(a => a.GetBySubjectAsync(AccountSubject)).ReturnsAsync(account);

            var ex = await Assert.ThrowsAsync<MagicLinkException>(() => service.VerifyAsync(rawToken));
            Assert.Equal(500, ex.StatusCode);

            // 解決失敗は消費の前なので、トークンは消費されない（設定を直せば再試行できる）。
            context.ChangeTracker.Clear();
            var token = await context.MagicLoginTokens.AsNoTracking().SingleAsync();
            Assert.Null(token.UsedAt);

            // トークンも発行されていない。
            tokenMock.Verify(
                t => t.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()),
                Times.Never);
        }
    }
}
