using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="UserService.ResolveFederatedUserAsync"/> の解決順（EcAuthDocs#122、設計書「ユーザーの解決と統合のルール」）。
    /// identity → 旧 ExternalIdpMapping（遅延移行）→ 確認済みメールアドレスでの統合 → 新規作成。
    /// 一意制約が絡むケースは <see cref="UserServiceRelationalTests"/>（SQLite）で見る。
    /// </summary>
    public sealed class UserServiceFederatedLoginTests : IDisposable
    {
        private const int OrgId = 10;
        private const int OtherOrgId = 20;

        private readonly string _dbName = Guid.NewGuid().ToString();
        private readonly EcAuthDbContext _context;
        private readonly UserService _service;

        private readonly Client _client;
        private readonly Client _otherClientSameOrg;
        private readonly OpenIdProvider _google;
        private readonly OpenIdProvider _line;
        private readonly OpenIdProvider _otherClientGoogle;

        public UserServiceFederatedLoginTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext(_dbName);
            _context.Organizations.Add(new Organization { Id = OrgId, Code = "org-a", Name = "A", TenantName = "test-tenant" });
            _context.Organizations.Add(new Organization { Id = OtherOrgId, Code = "org-b", Name = "B", TenantName = "test-tenant" });
            _client = new Client { Id = 1, ClientId = "client-a", ClientSecret = "s", AppName = "A", OrganizationId = OrgId };
            _otherClientSameOrg = new Client { Id = 2, ClientId = "client-a2", ClientSecret = "s", AppName = "A2", OrganizationId = OrgId };
            _context.Clients.AddRange(_client, _otherClientSameOrg);
            _google = NewProvider(101, "google", _client.Id);
            _line = NewProvider(102, "line", _client.Id);
            _otherClientGoogle = NewProvider(201, "google", _otherClientSameOrg.Id);
            _context.OpenIdProviders.AddRange(_google, _line, _otherClientGoogle);
            _context.SaveChanges();
            _service = new TestableUserService(_context, new Mock<ILogger<UserService>>().Object);
        }

        private static OpenIdProvider NewProvider(int id, string name, int clientId) => new()
        {
            Id = id,
            Name = name,
            IdpClientId = "idp",
            IdpClientSecret = "secret",
            ClientId = clientId
        };

        private Task<EcAuthUser> Login(OpenIdProvider provider, string sub, string? email = null, bool verified = false, Client? client = null) =>
            _service.ResolveFederatedUserAsync(new IUserService.FederatedLoginRequest
            {
                Client = client ?? _client,
                Provider = provider,
                ExternalSubject = sub,
                Email = email,
                EmailVerified = verified
            });

        private Task<List<EcAuthUser>> Users() => _context.EcAuthUsers.IgnoreQueryFilters().ToListAsync();

        private Task<List<EcAuthUserIdentity>> Identities() => _context.EcAuthUserIdentities.IgnoreQueryFilters().ToListAsync();

        // ---- 新規作成 ----

        [Fact]
        public async Task NewUser_CreatesUserIdentityAndLegacyMappingInClientPool()
        {
            var user = await Login(_google, "Sub-1", "user@example.com", verified: true);

            Assert.Equal(OrgId, user.OrganizationId);
            Assert.Equal("client-a", user.ClientId);
            Assert.Equal(EmailHashUtil.HashEmail("user@example.com"), user.EmailHash);
            Assert.NotNull(user.EmailVerifiedAt);

            var identity = Assert.Single(await Identities());
            Assert.Equal(user.Subject, identity.EcAuthSubject);
            Assert.Equal("idp:101", identity.IssuerKey);
            Assert.Equal(ExternalIdHasher.HashCaseSensitive("Sub-1"), identity.ExternalId);
            Assert.Equal("client-a", identity.ClientId);

            // expand 期間の二重書き（ロールアウト中の旧インスタンス用）
            var mapping = Assert.Single(await _context.ExternalIdpMappings.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(("google", "Sub-1", user.Subject), (mapping.ExternalProvider, mapping.ExternalSubject, mapping.EcAuthSubject));
        }

        [Fact]
        public async Task NewUser_WithUnverifiedEmail_LeavesEmailVerifiedAtNull()
        {
            var user = await Login(_google, "sub-1", "user@example.com", verified: false);

            Assert.Equal(EmailHashUtil.HashEmail("user@example.com"), user.EmailHash);
            Assert.Null(user.EmailVerifiedAt);
        }

        [Fact]
        public async Task NewUser_WithoutEmail_HasEmptyEmailHashEvenIfVerifiedClaimed()
        {
            var user = await Login(_line, "U123", email: null, verified: true);

            Assert.Equal(string.Empty, user.EmailHash);
            Assert.Null(user.EmailVerifiedAt);
        }

        // ---- identity での解決 ----

        [Fact]
        public async Task SecondLogin_ResolvesSameUserWithoutDuplicates()
        {
            var first = await Login(_google, "sub-1", "user@example.com", verified: true);

            var second = await Login(_google, "sub-1", "user@example.com", verified: true);

            Assert.Equal(first.Subject, second.Subject);
            Assert.Single(await Users());
            Assert.Single(await Identities());
            Assert.Single(await _context.ExternalIdpMappings.IgnoreQueryFilters().ToListAsync());
        }

        [Fact]
        public async Task SubjectsDifferingOnlyByCase_AreDifferentUsers()
        {
            // OIDC Core §2: sub は case-sensitive。小文字化して同一視しない
            var upper = await Login(_google, "AbC");
            var lower = await Login(_google, "abc");

            Assert.NotEqual(upper.Subject, lower.Subject);
            Assert.Equal(2, (await Identities()).Count);
        }

        [Fact]
        public async Task SubjectsDifferingOnlyByWhitespace_AreDifferentUsers()
        {
            // sub は加工せずにハッシュする。前後の空白を落とすと別アカウントの identity に解決される
            var plain = await Login(_google, "account-1");
            var spaced = await Login(_google, " account-1");

            Assert.NotEqual(plain.Subject, spaced.Subject);
        }

        [Fact]
        public async Task SameSubjectFromAnotherProvider_IsDifferentUser()
        {
            var google = await Login(_google, "sub-1");
            var line = await Login(_line, "sub-1");

            Assert.NotEqual(google.Subject, line.Subject);
        }

        [Fact]
        public async Task SameSubjectInAnotherClientOfSameOrganization_IsDifferentUser()
        {
            // ユーザープールは Client 単位（1 サイト = 1 B2C Client）
            var a = await Login(_google, "sub-1", "user@example.com", verified: true);
            var a2 = await Login(_otherClientGoogle, "sub-1", "user@example.com", verified: true, client: _otherClientSameOrg);

            Assert.NotEqual(a.Subject, a2.Subject);
            Assert.Equal("client-a2", a2.ClientId);
        }

        [Fact]
        public async Task Resolution_DoesNotDependOnRequestTenant()
        {
            // /v1/auth/callback は固定のホストへ戻るため、リクエストのテナントはログイン先と一致しない。
            // issuer_key（= Client）で引くので、テナントが違っても同じユーザーに解決する。
            var first = await Login(_google, "sub-1");

            var otherTenant = new MockTenantService();
            otherTenant.SetTenant("callback-host-tenant");
            using var context = TestDbContextHelper.CreateInMemoryContext(_dbName, otherTenant);
            var service = new UserService(context, new Mock<ILogger<UserService>>().Object);
            var client = await context.Clients.SingleAsync(c => c.Id == _client.Id);
            var provider = await context.OpenIdProviders.IgnoreQueryFilters().SingleAsync(p => p.Id == _google.Id);

            var second = await service.ResolveFederatedUserAsync(new IUserService.FederatedLoginRequest
            {
                Client = client,
                Provider = provider,
                ExternalSubject = "sub-1"
            });

            Assert.Equal(first.Subject, second.Subject);
        }

        [Fact]
        public async Task IdentityPointingToAnotherPool_IsRejected()
        {
            // データ不整合（identity の issuer_key と、ユーザーの Client が食い違う）は別プールの
            // ユーザーとしてログインさせない
            var user = await Login(_google, "sub-1");
            var stored = await _context.EcAuthUsers.IgnoreQueryFilters().SingleAsync(u => u.Subject == user.Subject);
            stored.ClientId = "client-a2";
            await _context.SaveChangesAsync();

            await Assert.ThrowsAsync<UserPoolMismatchException>(() => Login(_google, "sub-1"));
        }

        // ---- 旧 ExternalIdpMapping からの遅延移行 ----

        private async Task<EcAuthUser> SeedLegacyUserAsync(string subject, string provider, string externalSubject, string? clientId = null, int orgId = OrgId)
        {
            var user = new EcAuthUser { Subject = subject, EmailHash = string.Empty, OrganizationId = orgId, ClientId = clientId };
            _context.EcAuthUsers.Add(user);
            _context.ExternalIdpMappings.Add(new ExternalIdpMapping
            {
                EcAuthSubject = subject,
                ExternalProvider = provider,
                ExternalSubject = externalSubject,
                EcAuthUser = user
            });
            await _context.SaveChangesAsync();
            return user;
        }

        [Fact]
        public async Task LegacyMappingOnly_ResolvesSameUserAndMigratesToIdentity()
        {
            await SeedLegacyUserAsync("legacy-1", "google", "sub-1");

            var user = await Login(_google, "sub-1", "user@example.com", verified: true);

            Assert.Equal("legacy-1", user.Subject);
            Assert.Equal("client-a", user.ClientId);
            var identity = Assert.Single(await Identities());
            Assert.Equal(("legacy-1", "idp:101"), (identity.EcAuthSubject, identity.IssuerKey));
            Assert.Single(await Users());
        }

        [Fact]
        public async Task LegacyMappingOfAnotherClient_IsNotUsed()
        {
            // provider 名は Client 間で衝突しうる。別 Client のユーザーへフォールバックしない
            await SeedLegacyUserAsync("legacy-a2", "google", "sub-1", clientId: "client-a2");

            var user = await Login(_google, "sub-1");

            Assert.NotEqual("legacy-a2", user.Subject);
            Assert.Equal("client-a", user.ClientId);
        }

        [Fact]
        public async Task LegacyMappingInAnotherOrganization_IsNotUsed()
        {
            await SeedLegacyUserAsync("legacy-org-b", "google", "sub-1", orgId: OtherOrgId);

            var user = await Login(_google, "sub-1");

            Assert.NotEqual("legacy-org-b", user.Subject);
            Assert.Equal(OrgId, user.OrganizationId);
            // 旧構造の一意制約は Organization をまたぐので、既にある (google, sub-1) は書かない
            var mapping = Assert.Single(await _context.ExternalIdpMappings.IgnoreQueryFilters().ToListAsync());
            Assert.Equal("legacy-org-b", mapping.EcAuthSubject);
        }

        // ---- 確認済みメールアドレスでの統合 ----

        [Fact]
        public async Task VerifiedEmailMatchingVerifiedUser_LinksIdentityToExistingUser()
        {
            var google = await Login(_google, "g-sub", "user@example.com", verified: true);

            var line = await Login(_line, "l-sub", "User@Example.com", verified: true);

            Assert.Equal(google.Subject, line.Subject);
            Assert.Single(await Users());
            Assert.Equal(new[] { "idp:101", "idp:102" }, (await Identities()).Select(i => i.IssuerKey).OrderBy(k => k));
        }

        [Fact]
        public async Task UnverifiedIncomingEmail_DoesNotLink()
        {
            var google = await Login(_google, "g-sub", "user@example.com", verified: true);

            var line = await Login(_line, "l-sub", "user@example.com", verified: false);

            Assert.NotEqual(google.Subject, line.Subject);
        }

        [Fact]
        public async Task UnverifiedExistingUser_IsNotLinkTarget()
        {
            var google = await Login(_google, "g-sub", "user@example.com", verified: false);

            var line = await Login(_line, "l-sub", "user@example.com", verified: true);

            Assert.NotEqual(google.Subject, line.Subject);
        }

        [Fact]
        public async Task VerifiedEmailInAnotherClient_DoesNotLink()
        {
            var a = await Login(_google, "g-sub", "user@example.com", verified: true);

            var a2 = await Login(_otherClientGoogle, "other-sub", "user@example.com", verified: true, client: _otherClientSameOrg);

            Assert.NotEqual(a.Subject, a2.Subject);
        }

        [Fact]
        public async Task MultipleVerifiedCandidates_DoNotLink()
        {
            // 既存データに確認済みの同じメールアドレスが 2 人いる（統合導入前の行など）
            var hash = EmailHashUtil.HashEmail("user@example.com");
            foreach (var subject in new[] { "dup-1", "dup-2" })
            {
                _context.EcAuthUsers.Add(new EcAuthUser
                {
                    Subject = subject,
                    EmailHash = hash,
                    EmailVerifiedAt = DateTimeOffset.UtcNow,
                    OrganizationId = OrgId,
                    ClientId = "client-a"
                });
            }
            await _context.SaveChangesAsync();

            var user = await Login(_google, "g-sub", "user@example.com", verified: true);

            Assert.DoesNotContain(user.Subject, new[] { "dup-1", "dup-2" });
            Assert.Equal(3, (await Users()).Count);
        }

        // ---- メールアドレスの反映 ----

        [Fact]
        public async Task LoginWithoutEmail_KeepsEmailObtainedByAnotherIdentity()
        {
            var google = await Login(_google, "g-sub", "user@example.com", verified: true);
            // LINE を同じユーザーに紐付けたうえで、LINE はメールアドレスを返さない
            _context.EcAuthUserIdentities.Add(new EcAuthUserIdentity
            {
                EcAuthSubject = google.Subject,
                IssuerKey = B2CIssuerKey.ForIdp(_line.Id),
                ExternalId = ExternalIdHasher.HashCaseSensitive("l-sub"),
                ClientId = "client-a"
            });
            await _context.SaveChangesAsync();

            var user = await Login(_line, "l-sub", email: null);

            Assert.Equal(google.Subject, user.Subject);
            Assert.Equal(EmailHashUtil.HashEmail("user@example.com"), user.EmailHash);
            Assert.NotNull(user.EmailVerifiedAt);
        }

        [Fact]
        public async Task ChangedEmail_UpdatesHashAndTakesNewVerificationState()
        {
            await Login(_google, "g-sub", "old@example.com", verified: true);

            var user = await Login(_google, "g-sub", "new@example.com", verified: false);

            Assert.Equal(EmailHashUtil.HashEmail("new@example.com"), user.EmailHash);
            Assert.Null(user.EmailVerifiedAt);
        }

        [Fact]
        public async Task SameEmailLaterVerified_FillsVerifiedAtAndUnverifiedClaimDoesNotDowngrade()
        {
            await Login(_google, "g-sub", "user@example.com", verified: false);

            var verified = await Login(_google, "g-sub", "user@example.com", verified: true);
            Assert.NotNull(verified.EmailVerifiedAt);

            var again = await Login(_google, "g-sub", "user@example.com", verified: false);
            Assert.NotNull(again.EmailVerifiedAt);
        }

        // ---- 入力の検証 ----

        [Fact]
        public async Task ProviderOfAnotherClient_IsRejected()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Login(_otherClientGoogle, "sub-1"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task EmptySubject_IsRejected(string sub)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Login(_google, sub));
        }

        [Fact]
        public async Task ClientWithoutOrganization_IsRejected()
        {
            var orphan = new Client { Id = 3, ClientId = "orphan", ClientSecret = "s", AppName = "O", OrganizationId = null };
            var provider = NewProvider(301, "google", orphan.Id);

            await Assert.ThrowsAsync<ArgumentException>(() => Login(provider, "sub-1", client: orphan));
        }

        public void Dispose() => _context.Dispose();
    }
}
