using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// b2b_user_identity（発行元ごとの識別子）の振る舞いを検証する（EcAuthDocs#110）。
    ///
    /// 中心にあるのは「external_id は発行元をまたぐと衝突するが、issuer_key が名前空間として
    /// 分離するので同一 external_id でも別ユーザーとして共存できる」という性質。
    /// </summary>
    public class B2BUserIdentityTests : IDisposable
    {
        private const string EcCubeIssuer = "client:ec-shop-eccube";
        private const string WordPressIssuer = "client:ec-shop-wordpress";

        private readonly EcAuthDbContext _context;
        private readonly B2BUserService _service;

        public B2BUserIdentityTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _service = new B2BUserService(_context, new Mock<ILogger<B2BUserService>>().Object);

            _context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            });
            _context.SaveChanges();
        }

        private Task<IB2BUserService.CreateUserResult> CreateAsync(
            string externalId, string issuerKey, string? clientId = null, string? subject = null)
            => _service.CreateAsync(new IB2BUserService.CreateUserRequest
            {
                Subject = subject,
                ExternalId = externalId,
                IssuerKey = issuerKey,
                ClientId = clientId ?? issuerKey[B2BIssuerKey.ClientPrefix.Length..],
                UserType = "admin",
                OrganizationId = 1
            });

        [Fact]
        public async Task CreateAsync_ShouldAlsoCreateIdentityRow()
        {
            var result = await CreateAsync("1", EcCubeIssuer);

            var identity = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .SingleAsync(i => i.B2BSubject == result.User.Subject);

            Assert.Equal(EcCubeIssuer, identity.IssuerKey);
            Assert.Equal("ec-shop-eccube", identity.ClientId);
            // 平文ではなく正規化 + SHA-256 ハッシュで保持する（個人情報非保持要件）。
            Assert.Equal(ExternalIdHasher.Hash("1"), identity.ExternalId);
            Assert.NotEqual("1", identity.ExternalId);
        }

        [Fact]
        public async Task CreateAsync_WithoutIssuerKey_ShouldThrowArgumentException()
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateAsync(new IB2BUserService.CreateUserRequest
                {
                    ExternalId = "1",
                    IssuerKey = "",
                    UserType = "admin",
                    OrganizationId = 1
                }));

            Assert.Contains("IssuerKey", ex.Message);
        }

        [Fact]
        public async Task GetByIdentityAsync_ShouldResolveUser()
        {
            var created = await CreateAsync("42", EcCubeIssuer);

            var resolved = await _service.GetByIdentityAsync(EcCubeIssuer, "42");

            Assert.NotNull(resolved);
            Assert.Equal(created.User.Subject, resolved.Subject);
        }

        [Fact]
        public async Task GetByIdentityAsync_WithDifferentIssuerKey_ShouldReturnNull()
        {
            await CreateAsync("42", EcCubeIssuer);

            // 同じ external_id でも発行元が違えば解決してはいけない。
            var resolved = await _service.GetByIdentityAsync(WordPressIssuer, "42");

            Assert.Null(resolved);
        }

        /// <summary>
        /// #110 問題 2 の核心。EC-CUBE の member_id=1 と WordPress の user_id=1 は
        /// 正規化 + SHA-256 の結果が同一になるが、issuer_key が違うため別人として共存できる。
        ///
        /// 旧来の一意制約 (organization_id, external_id) ではこの 2 件は同一 Organization 内で
        /// 衝突し、別人が同一 B2BUser に解決されていた。
        /// </summary>
        [Fact]
        public async Task SameExternalId_UnderDifferentIssuers_ShouldResolveToDifferentUsers()
        {
            var ecCubeAdmin = await CreateAsync("1", EcCubeIssuer);
            var wordPressAdmin = await CreateAsync("1", WordPressIssuer);

            Assert.NotEqual(ecCubeAdmin.User.Subject, wordPressAdmin.User.Subject);

            // 前提の確認: ハッシュ値そのものは同一（分離しているのはハッシュではない）。
            var hashes = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .Select(i => i.ExternalId)
                .Distinct()
                .ToListAsync();
            Assert.Single(hashes);

            // それでも issuer_key で引き分けられる。
            var resolvedEcCube = await _service.GetByIdentityAsync(EcCubeIssuer, "1");
            var resolvedWordPress = await _service.GetByIdentityAsync(WordPressIssuer, "1");

            Assert.Equal(ecCubeAdmin.User.Subject, resolvedEcCube?.Subject);
            Assert.Equal(wordPressAdmin.User.Subject, resolvedWordPress?.Subject);
        }

        /// <summary>
        /// 識別子が変わっても旧行は削除せず共存させる（EcAuthDocs#110 の「移行トリガーは不要」）。
        /// これにより、プラグイン更新前に登録されたユーザーも引き続き解決できる。
        /// </summary>
        [Fact]
        public async Task EnsureIdentityAsync_WhenExternalIdChanged_ShouldKeepOldIdentity()
        {
            var created = await CreateAsync("old-login-id", EcCubeIssuer);

            await _service.EnsureIdentityAsync(
                created.User.Subject, EcCubeIssuer, "new-member-id", "ec-shop-eccube");

            var identities = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .Where(i => i.B2BSubject == created.User.Subject)
                .Select(i => i.ExternalId)
                .ToListAsync();

            Assert.Equal(2, identities.Count);
            Assert.Contains(ExternalIdHasher.Hash("old-login-id"), identities);
            Assert.Contains(ExternalIdHasher.Hash("new-member-id"), identities);

            // 新旧どちらの識別子でも同一ユーザーに解決できる。
            Assert.Equal(created.User.Subject,
                (await _service.GetByIdentityAsync(EcCubeIssuer, "old-login-id"))?.Subject);
            Assert.Equal(created.User.Subject,
                (await _service.GetByIdentityAsync(EcCubeIssuer, "new-member-id"))?.Subject);
        }

        [Fact]
        public async Task EnsureIdentityAsync_WhenAlreadyPresent_ShouldBeNoOp()
        {
            var created = await CreateAsync("1", EcCubeIssuer);

            await _service.EnsureIdentityAsync(created.User.Subject, EcCubeIssuer, "1", "ec-shop-eccube");

            var count = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .CountAsync(i => i.B2BSubject == created.User.Subject);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task EnsureIdentityAsync_OwnedByAnotherUser_ShouldThrowConflict()
        {
            var owner = await CreateAsync("1", EcCubeIssuer);
            var other = await CreateAsync("2", EcCubeIssuer);

            // other を、既に owner が持っている (issuer_key, external_id) に紐づけようとする。
            var ex = await Assert.ThrowsAsync<ExternalIdConflictException>(() =>
                _service.EnsureIdentityAsync(other.User.Subject, EcCubeIssuer, "1", "ec-shop-eccube"));

            // 例外メッセージに平文 external_id を含めない（PII をログに残さない）。
            Assert.DoesNotContain("'1'", ex.Message);
            Assert.Contains(ExternalIdHasher.Hash("1"), ex.Message);

            // 衝突時は行を増やさない。
            Assert.Equal(1, await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .CountAsync(i => i.B2BSubject == other.User.Subject));
            Assert.Equal(owner.User.Subject,
                (await _service.GetByIdentityAsync(EcCubeIssuer, "1"))?.Subject);
        }

        /// <summary>
        /// 旧カラム経由のフォールバックは「別の発行元が既に取得済みのユーザー」を返してはならない。
        ///
        /// b2b_user.external_id は organization_id 単位の旧名前空間しか持たないため、これを無条件に
        /// 引くと発行元 B のリクエストに対して発行元 A のユーザーが返る。呼び出し元
        /// （B2BPasskeyService）はその戻り値に EnsureIdentityAsync を実行するため、
        /// 別人が 1 つの b2b_subject へ恒久統合される。
        /// </summary>
        [Fact]
        public async Task GetUnclaimedByExternalIdAsync_ClaimedByAnotherIssuer_ShouldReturnNull()
        {
            await CreateAsync("1", EcCubeIssuer);

            var resolved = await _service.GetUnclaimedByExternalIdAsync("1", 1, WordPressIssuer);

            Assert.Null(resolved);
        }

        /// <summary>
        /// identity 行を持たない移行前ユーザー（Client が 0 個 / 複数ある Organization のため
        /// backfill の対象外だったユーザー）は、従来どおり旧カラム経由で解決できる。
        /// </summary>
        [Fact]
        public async Task GetUnclaimedByExternalIdAsync_WithoutAnyIdentity_ShouldReturnUser()
        {
            var created = await CreateAsync("1", EcCubeIssuer);

            // backfill 対象外だった状態を再現する
            _context.B2BUserIdentities.RemoveRange(
                await _context.B2BUserIdentities.IgnoreQueryFilters().ToListAsync());
            await _context.SaveChangesAsync();

            var resolved = await _service.GetUnclaimedByExternalIdAsync("1", 1, WordPressIssuer);

            Assert.Equal(created.User.Subject, resolved?.Subject);
        }

        /// <summary>
        /// 自分の発行元が既に持っているユーザーはフォールバック対象として妥当
        /// （identity の external_id が旧値のまま、b2b_user 側だけ同期済みのケース）。
        /// </summary>
        [Fact]
        public async Task GetUnclaimedByExternalIdAsync_ClaimedBySameIssuer_ShouldReturnUser()
        {
            var created = await CreateAsync("1", EcCubeIssuer);

            var resolved = await _service.GetUnclaimedByExternalIdAsync("1", 1, EcCubeIssuer);

            Assert.Equal(created.User.Subject, resolved?.Subject);
        }

        /// <summary>
        /// スキーマ契約の検証。b2b_user 側の (organization_id, external_id) は
        /// **一意であってはならない**（EcAuthDocs#110）。
        ///
        /// 一意のまま残すと、発行元の異なる同一 external_id の 2 人目を作る際に実 DB では
        /// b2b_user の INSERT が一意違反で落ち、本移行の目的が成立しない。InMemory プロバイダーは
        /// 一意インデックスを強制しないため <see cref="SameExternalId_UnderDifferentIssuers_ShouldResolveToDifferentUsers"/>
        /// だけでは検出できず、モデル定義側で確認する必要がある。
        /// </summary>
        [Fact]
        public void Model_ShouldNotDeclareUniqueIndexOnOrganizationIdAndExternalId()
        {
            var entityType = _context.Model.FindEntityType(typeof(B2BUser));
            Assert.NotNull(entityType);

            var index = entityType.GetIndexes().SingleOrDefault(i =>
                i.Properties.Select(p => p.Name).SequenceEqual(
                    new[] { nameof(B2BUser.OrganizationId), nameof(B2BUser.ExternalId) }));

            Assert.NotNull(index);
            Assert.False(index.IsUnique);
        }

        /// <summary>
        /// スキーマ契約の検証。InMemory プロバイダーは一意インデックスを強制しないため、
        /// 実 DB で効く制約はモデル定義側で確認する。
        /// </summary>
        [Fact]
        public void Model_ShouldDeclareUniqueIndexOnIssuerKeyAndExternalId()
        {
            var entityType = _context.Model.FindEntityType(typeof(B2BUserIdentity));
            Assert.NotNull(entityType);

            var uniqueIndex = entityType.GetIndexes().SingleOrDefault(i =>
                i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(
                    new[] { nameof(B2BUserIdentity.IssuerKey), nameof(B2BUserIdentity.ExternalId) }));

            Assert.NotNull(uniqueIndex);
        }

        public void Dispose()
        {
            _context.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
