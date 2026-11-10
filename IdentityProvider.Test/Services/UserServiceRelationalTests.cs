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
    /// <see cref="UserService"/> の JIT プロビジョニングが、今のユニーク制約とどう噛み合うかを
    /// リレーショナルな SQLite（<see cref="RetryingSqliteContext"/>）で固定する。
    /// InMemory プロバイダーは一意インデックスを強制しないため、<see cref="UserServiceTests"/> では検出できない。
    ///
    /// <para>
    /// ここに並ぶのは<b>今の挙動（制約違反で例外になる）</b>であって、あるべき挙動ではない。
    /// EcAuthDocs#122（B2C ユーザープールの Client 単位分離）で
    /// <c>UNIQUE (organization_id, email_hash)</c> を外し、ExternalIdpMapping の解決を
    /// <c>ecauth_user_identity</c> に移すときに、期待値を反転させる（EcAuthDocs#164 段 2）。
    /// </para>
    /// </summary>
    public sealed class UserServiceRelationalTests : IDisposable
    {
        private const int OrganizationId = 1;
        private const int OtherOrganizationId = 2;

        private readonly RetryingSqliteContext _db;
        private readonly UserService _service;

        public UserServiceRelationalTests()
        {
            _db = new RetryingSqliteContext();
            _db.Context.Organizations.Add(new Organization { Id = OrganizationId, Code = "org-a", Name = "Org A", TenantName = "test-tenant" });
            _db.Context.Organizations.Add(new Organization { Id = OtherOrganizationId, Code = "org-b", Name = "Org B", TenantName = "test-tenant" });
            _db.Context.SaveChanges();
            _service = new UserService(_db.Context, new Mock<ILogger<UserService>>().Object);
        }

        private static ExternalUserInfo External(string provider, string subject, string? email) => new()
        {
            Provider = provider,
            Subject = subject,
            Email = email ?? string.Empty
        };

        [Fact]
        public async Task SameProviderAndSubject_InSameOrganization_ReusesUser()
        {
            var first = await _service.CreateOrUpdateFromExternalAsync(External("google", "sub-1", "user@example.com"), OrganizationId);

            var second = await _service.CreateOrUpdateFromExternalAsync(External("google", "sub-1", "user@example.com"), OrganizationId);

            Assert.Equal(first.Subject, second.Subject);
            Assert.Equal(1, await _db.Context.EcAuthUsers.CountAsync());
            Assert.Equal(1, await _db.Context.ExternalIdpMappings.CountAsync());
        }

        [Fact]
        public async Task TwoUsersWithoutEmail_InSameOrganization_ViolateEmailHashUniqueIndex()
        {
            // メールアドレスを返さない IdP では EmailHash が空文字列になり、
            // UNIQUE (organization_id, email_hash) で 2 人目が作れない。#122 で反転させる。
            await _service.CreateOrUpdateFromExternalAsync(External("line", "sub-1", null), OrganizationId);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                _service.CreateOrUpdateFromExternalAsync(External("line", "sub-2", null), OrganizationId));
        }

        [Fact]
        public async Task SameEmailFromAnotherProvider_InSameOrganization_ViolatesEmailHashUniqueIndex()
        {
            // 同じメールアドレスで別の IdP から入ると、統合も別ユーザー作成もできずに失敗する。
            // #122 では「確認済みメールアドレス同士だけ自動で統合する」に変える。
            await _service.CreateOrUpdateFromExternalAsync(External("google", "g-sub", "user@example.com"), OrganizationId);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                _service.CreateOrUpdateFromExternalAsync(External("line", "l-sub", "user@example.com"), OrganizationId));
        }

        [Fact]
        public async Task SameProviderAndSubject_InAnotherOrganization_ViolatesMappingUniqueIndex()
        {
            // 解決は Organization で絞るが、UNIQUE (external_provider, external_subject) は全体で効くため、
            // 別 Organization では新規作成に進んで制約違反になる。ExternalProvider には OpenIdProvider.Name
            // （seeder では Client の app_name）が入るので、名前が衝突したときに起きる。#122 で反転させる。
            await _service.CreateOrUpdateFromExternalAsync(External("google", "sub-1", "a@example.com"), OrganizationId);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                _service.CreateOrUpdateFromExternalAsync(External("google", "sub-1", "b@example.com"), OtherOrganizationId));
        }

        [Fact]
        public async Task EmailChangeToAddressUsedByAnotherUser_ViolatesEmailHashUniqueIndex()
        {
            // 既存ユーザーの EmailHash 更新でも同じ制約に当たる
            await _service.CreateOrUpdateFromExternalAsync(External("google", "sub-1", "a@example.com"), OrganizationId);
            await _service.CreateOrUpdateFromExternalAsync(External("google", "sub-2", "b@example.com"), OrganizationId);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                _service.CreateOrUpdateFromExternalAsync(External("google", "sub-2", "a@example.com"), OrganizationId));
        }

        public void Dispose() => _db.Dispose();
    }
}
