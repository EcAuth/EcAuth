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
    /// <see cref="UserService"/> の JIT プロビジョニングを、一意制約を強制するリレーショナルな SQLite
    /// （<see cref="RetryingSqliteContext"/>）で検証する。InMemory プロバイダーは一意インデックスを強制しない。
    ///
    /// <para>
    /// EcAuthDocs#164 段 2 では、旧 <c>UNIQUE (organization_id, email_hash)</c> と
    /// <c>UNIQUE (external_provider, external_subject)</c> に当たって例外になる挙動をここで固定していた。
    /// EcAuthDocs#122 で email_hash を一意でなくし、識別子を <c>ecauth_user_identity</c>（issuer_key は
    /// OpenIdProvider の行 = Client）に移したので、同じケースが例外にならず正しいユーザーに解決されることを確かめる。
    /// </para>
    /// </summary>
    public sealed class UserServiceRelationalTests : IDisposable
    {
        private const int OrganizationId = 10;
        private const int OtherOrganizationId = 20;

        private readonly RetryingSqliteContext _db;
        private readonly UserService _service;
        private readonly Client _client;
        private readonly Client _otherOrgClient;
        private readonly OpenIdProvider _google;
        private readonly OpenIdProvider _line;
        private readonly OpenIdProvider _otherOrgGoogle;

        public UserServiceRelationalTests()
        {
            _db = new RetryingSqliteContext();
            var ctx = _db.Context;
            ctx.Organizations.Add(new Organization { Id = OrganizationId, Code = "org-a", Name = "Org A", TenantName = "test-tenant" });
            ctx.Organizations.Add(new Organization { Id = OtherOrganizationId, Code = "org-b", Name = "Org B", TenantName = "test-tenant" });
            _client = new Client { Id = 1, ClientId = "client-a", ClientSecret = "s", AppName = "A", OrganizationId = OrganizationId };
            _otherOrgClient = new Client { Id = 2, ClientId = "client-b", ClientSecret = "s", AppName = "B", OrganizationId = OtherOrganizationId };
            ctx.Clients.AddRange(_client, _otherOrgClient);
            // provider 名が Organization をまたいで衝突する（旧構造で制約違反になっていた条件）
            _google = new OpenIdProvider { Id = 101, Name = "google", IdpClientId = "x", IdpClientSecret = "y", ClientId = _client.Id };
            _line = new OpenIdProvider { Id = 102, Name = "line", IdpClientId = "x", IdpClientSecret = "y", ClientId = _client.Id };
            _otherOrgGoogle = new OpenIdProvider { Id = 201, Name = "google", IdpClientId = "x", IdpClientSecret = "y", ClientId = _otherOrgClient.Id };
            ctx.OpenIdProviders.AddRange(_google, _line, _otherOrgGoogle);
            ctx.SaveChanges();
            _service = new UserService(ctx, new Mock<ILogger<UserService>>().Object);
        }

        private Task<EcAuthUser> Login(OpenIdProvider provider, string sub, string? email, bool verified = false, Client? client = null) =>
            _service.ResolveFederatedUserAsync(new IUserService.FederatedLoginRequest
            {
                Client = client ?? _client,
                Provider = provider,
                ExternalSubject = sub,
                Email = email,
                EmailVerified = verified
            });

        [Fact]
        public async Task SameProviderAndSubject_InSameClient_ReusesUser()
        {
            var first = await Login(_google, "sub-1", "user@example.com");

            var second = await Login(_google, "sub-1", "user@example.com");

            Assert.Equal(first.Subject, second.Subject);
            Assert.Equal(1, await _db.Context.EcAuthUsers.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await _db.Context.EcAuthUserIdentities.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task TwoUsersWithoutEmail_InSameClient_AreCreated()
        {
            // 旧: UNIQUE (organization_id, email_hash) で 2 人目が DbUpdateException
            var first = await Login(_line, "sub-1", null);

            var second = await Login(_line, "sub-2", null);

            Assert.NotEqual(first.Subject, second.Subject);
            Assert.All(new[] { first, second }, u => Assert.Equal(string.Empty, u.EmailHash));
        }

        [Fact]
        public async Task SameUnverifiedEmailFromAnotherProvider_CreatesSeparateUser()
        {
            // 旧: 同じ制約で DbUpdateException。未確認のメールアドレスでは統合しないので別ユーザーになる
            var google = await Login(_google, "g-sub", "user@example.com");

            var line = await Login(_line, "l-sub", "user@example.com");

            Assert.NotEqual(google.Subject, line.Subject);
            Assert.Equal(google.EmailHash, line.EmailHash);
        }

        [Fact]
        public async Task SameVerifiedEmailFromAnotherProvider_LinksToSameUser()
        {
            var google = await Login(_google, "g-sub", "user@example.com", verified: true);

            var line = await Login(_line, "l-sub", "user@example.com", verified: true);

            Assert.Equal(google.Subject, line.Subject);
            Assert.Equal(2, await _db.Context.EcAuthUserIdentities.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task SameProviderNameAndSubject_InAnotherOrganization_CreatesSeparateUser()
        {
            // 旧: UNIQUE (external_provider, external_subject) が Organization をまたいで効き DbUpdateException。
            // identity の issuer_key は OpenIdProvider の行（idp:101 / idp:201）なので衝突しない。
            // 二重書きは旧構造に既に行がある側を書かない（書けば旧制約に当たる）。
            var a = await Login(_google, "sub-1", "a@example.com");

            var b = await Login(_otherOrgGoogle, "sub-1", "b@example.com", client: _otherOrgClient);

            Assert.NotEqual(a.Subject, b.Subject);
            Assert.Equal(OtherOrganizationId, b.OrganizationId);
            Assert.Equal(1, await _db.Context.ExternalIdpMappings.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task EmailChangeToAddressUsedByAnotherUser_IsAllowed()
        {
            // 旧: EmailHash の更新でも同じ制約で DbUpdateException
            await Login(_google, "sub-1", "a@example.com");
            await Login(_google, "sub-2", "b@example.com");

            var changed = await Login(_google, "sub-2", "a@example.com");

            Assert.Equal(EmailHashUtil.HashEmail("a@example.com"), changed.EmailHash);
        }

        [Fact]
        public async Task IdentityUniqueIndex_RejectsDuplicateIssuerAndExternalId()
        {
            // 並行した初回ログインの重複作成は (issuer_key, external_id) の一意制約が止める
            var user = await Login(_google, "sub-1", null);
            _db.Context.EcAuthUserIdentities.Add(new EcAuthUserIdentity
            {
                EcAuthSubject = user.Subject,
                IssuerKey = B2CIssuerKey.ForIdp(_google.Id),
                ExternalId = ExternalIdHasher.HashCaseSensitive("sub-1"),
                ClientId = "client-a"
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
        }

        // ---- 旧ユーザーのプール確定（client_id が null の旧ユーザー）----

        private async Task SeedLegacyUserAsync(string subject, string provider, string externalSubject)
        {
            var user = new EcAuthUser { Subject = subject, EmailHash = string.Empty, OrganizationId = OrganizationId, ClientId = null };
            _db.Context.EcAuthUsers.Add(user);
            _db.Context.ExternalIdpMappings.Add(new ExternalIdpMapping
            {
                EcAuthSubject = subject,
                ExternalProvider = provider,
                ExternalSubject = externalSubject,
                EcAuthUser = user
            });
            await _db.Context.SaveChangesAsync();
            _db.Context.ChangeTracker.Clear();
        }

        [Fact]
        public async Task LegacyUserWithoutClient_IsClaimedByConditionalUpdate()
        {
            await SeedLegacyUserAsync("legacy-1", "google", "sub-1");

            var user = await Login(_google, "sub-1", null);

            Assert.Equal("legacy-1", user.Subject);
            await using var check = _db.CreateSiblingContext();
            var stored = await check.EcAuthUsers.IgnoreQueryFilters().SingleAsync(u => u.Subject == "legacy-1");
            Assert.Equal("client-a", stored.ClientId);
        }

        [Fact]
        public async Task LegacyUserClaimedConcurrentlyByAnotherClient_IsNotShared()
        {
            // 別 Client からのログインが、こちらが読んだ直後にプールを確定させた状況を再現する
            await SeedLegacyUserAsync("legacy-1", "google", "sub-1");
            var racing = new ClaimRacedUserService(_db, "client-a2", new Mock<ILogger<UserService>>().Object);

            var user = await racing.ResolveFederatedUserAsync(new IUserService.FederatedLoginRequest
            {
                Client = _client,
                Provider = _google,
                ExternalSubject = "sub-1"
            });

            // 負けた側は旧ユーザーを自分のプールに取り込まず、自分の Client に新しいユーザーを作る
            Assert.NotEqual("legacy-1", user.Subject);
            Assert.Equal("client-a", user.ClientId);
            await using var check = _db.CreateSiblingContext();
            var legacy = await check.EcAuthUsers.IgnoreQueryFilters().SingleAsync(u => u.Subject == "legacy-1");
            Assert.Equal("client-a2", legacy.ClientId);
            Assert.False(await check.EcAuthUserIdentities.IgnoreQueryFilters().AnyAsync(i => i.EcAuthSubject == "legacy-1"));
        }

        /// <summary>条件付き UPDATE の直前に、別の要求（兄弟 context）がプールを確定させる。</summary>
        private sealed class ClaimRacedUserService : UserService
        {
            private readonly RetryingSqliteContext _db;
            private readonly string _otherClientId;
            private bool _raced;

            public ClaimRacedUserService(RetryingSqliteContext db, string otherClientId, ILogger<UserService> logger)
                : base(db.Context, logger)
            {
                _db = db;
                _otherClientId = otherClientId;
            }

            protected override async Task<int> TryClaimClientAsync(int userId, string clientId)
            {
                if (!_raced)
                {
                    _raced = true;
                    await using var other = _db.CreateSiblingContext();
                    await other.EcAuthUsers.IgnoreQueryFilters()
                        .Where(u => u.Id == userId && u.ClientId == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(u => u.ClientId, _otherClientId));
                }
                return await base.TryClaimClientAsync(userId, clientId);
            }
        }

        public void Dispose() => _db.Dispose();
    }
}
