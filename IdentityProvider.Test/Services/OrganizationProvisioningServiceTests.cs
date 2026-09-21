using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// 申込フローとマイページのサイト追加が共用するプロビジョニングロジックのテスト。
    /// 組織コードの導出規則と、ドメイン占有・削除済みドメインの扱いを固定する。
    /// </summary>
    public class OrganizationProvisioningServiceTests
    {
        private static OrganizationProvisioningService CreateService(EcAuthDbContext context)
            => new(context, new PlaintextSecretProtector());

        [Theory]
        [InlineData("https://shop.example.jp", false, "shop-example-jp")]
        [InlineData("https://www.shop.example.jp", false, "shop-example-jp")]
        [InlineData("https://shop.example.jp", true, "shop-example-jp-sandbox")]
        [InlineData("https://stg.example.jp", true, "stg-example-jp-sandbox")]
        public void BuildSite_DerivesOrganizationCode(string url, bool isSandbox, string expectedCode)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            var site = service.BuildSite(url, isSandbox, "site_url");

            Assert.Equal(expectedCode, site.Code);
            Assert.Equal(isSandbox, site.IsSandbox);
        }

        [Fact]
        public void BuildSite_NonHttpsUrl_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            var ex = Assert.Throws<SignupValidationException>(
                () => service.BuildSite("http://shop.example.jp", false, "site_url"));

            Assert.Equal("invalid_site_url", ex.Error);
        }

        /// <summary>
        /// 既存 Org を、そのサイトのホストを allowed_rp_ids に持つ Client 付きで投入する。
        /// ドメインの占有は Client のホスト単位で判定するため、Organization だけでは衝突しない。
        /// </summary>
        private static async Task SeedSiteAsync(
            EcAuthDbContext context, int orgId, string code, string[] hosts,
            bool isSandbox = false, DateTimeOffset? deletedAt = null, int? clientDbId = null)
        {
            if (!await context.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
            {
                context.Organizations.Add(new Organization
                {
                    Id = orgId,
                    Code = code,
                    Name = code,
                    TenantName = code,
                    IsSandbox = isSandbox,
                    DeletedAt = deletedAt
                });
            }
            context.Clients.Add(new Client
            {
                Id = clientDbId ?? orgId * 10,
                ClientId = $"ec-{code}-{clientDbId ?? orgId * 10}",
                ClientSecret = "secret",
                AppName = code,
                OrganizationId = orgId,
                SubjectType = SubjectType.B2B,
                AllowedRpIds = hosts.ToList()
            });
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_DomainOwnedByAnotherAccount_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: true, "site_url");

            // 他アカウントが本番として押さえているドメインは、サンドボックスとしても登録できない。
            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(new[] { site }, CancellationToken.None));

            Assert.Equal("organization_already_exists", ex.Error);
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_DomainOwnedByCaller_Passes()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: true, "site_url");

            // 自分の本番 Org と同じドメイン。組織コードは -sandbox で分かれ、ホストは管理下 Org の
            // Client が持つものなので通る。
            await service.EnsureOrganizationCodesAvailableAsync(
                new[] { site }, CancellationToken.None, ownedOrganizationIds: new[] { 1 });
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_SameCodeAsOwnOrganization_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url");

            // 自分の Org でも、まったく同じ組織コードは作れない（unique 制約に触れる）。
            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(
                    new[] { site }, CancellationToken.None, ownedOrganizationIds: new[] { 1 }));

            Assert.Equal("organization_already_exists", ex.Error);
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_DeletedDomain_ThrowsOrganizationDeleted()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            // 削除済み Org は管理対象から外れる（ownedOrganizationIds に載らない）。組織コードは
            // 申込側と衝突しない値にして、ホスト単位の判定で検知されることを確かめる。
            await SeedSiteAsync(context, 1, "old-shop", new[] { "shop.example.jp" },
                deletedAt: DateTimeOffset.UtcNow);

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp/", isSandbox: false, "site_url");

            // 論理削除してもホストは解放しない（課金集計で期間が混ざるため）。
            // 「他人が使っている」のとは理由が違うので、エラーコードで区別する。
            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(new[] { site }, CancellationToken.None));

            Assert.Equal("organization_deleted", ex.Error);
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_OrganizationWithoutClient_DoesNotOccupyHost()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            // Client を持たない Org（別コード）。ホストの占有は Client 単位なので衝突しない。
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "other-code",
                Name = "Other",
                TenantName = "other-code"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url");

            await service.EnsureOrganizationCodesAvailableAsync(new[] { site }, CancellationToken.None);
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_CodeTooLong_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            // 組織コードはテナント名（DNS ラベル 1 つ）になるため 63 文字を超えられない。
            var longHost = new string('a', 60) + ".example.jp";
            var site = service.BuildSite($"https://{longHost}", isSandbox: false, "site_url");

            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(new[] { site }, CancellationToken.None));

            Assert.Equal("invalid_site_url", ex.Error);
        }

        // ---- ホスト単位の占有チェック（EcAuthDocs#121 項目 1）----

        [Fact]
        public async Task EnsureHostsAvailableAsync_HostOwnedBySecondClientOfAnotherOrganization_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            // 組織コードは最初のサイト由来。2 つ目の Client のホストはどの組織コードにも写らない。
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "wp.example.jp" }, clientDbId: 11);

            var service = CreateService(context);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureHostsAvailableAsync(new[] { "wp.example.jp" }, CancellationToken.None));

            Assert.Equal("organization_already_exists", ex.Error);
            Assert.Equal("site_url", ex.Field);
        }

        [Fact]
        public async Task EnsureHostsAvailableAsync_HostOwnedByManagedOrganization_Passes()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);

            // 同一 Organization に同じホストの Client を足す（EC-CUBE と WordPress の同居）。
            await service.EnsureHostsAvailableAsync(
                new[] { "shop.example.jp" }, CancellationToken.None, ownedOrganizationIds: new[] { 1 });
        }

        [Fact]
        public async Task EnsureHostsAvailableAsync_ExactMatchOnly_DoesNotBlockSubdomainOrSuperstring()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "example-jp", new[] { "example.jp" });

            var service = CreateService(context);

            // 判定は完全一致。apex を持つ Org が居てもサブドメインは占有されない（eTLD+1 判定は別 issue）。
            // LIKE のプリフィルタが "example.jp" の部分一致で拾っても、完全一致で確定するので通る。
            await service.EnsureHostsAvailableAsync(new[] { "shop.example.jp" }, CancellationToken.None);
            await service.EnsureHostsAvailableAsync(new[] { "myexample.jp" }, CancellationToken.None);
        }

        [Fact]
        public async Task EnsureHostsAvailableAsync_MatchesCaseInsensitively()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            // 保存値は小文字に正規化されている前提だが、万一大文字が混ざっていても占有として扱う。
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "Shop.Example.JP" });

            var service = CreateService(context);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureHostsAvailableAsync(new[] { "shop.example.jp" }, CancellationToken.None));

            Assert.Equal("organization_already_exists", ex.Error);
        }

        [Fact]
        public async Task EnsureHostsAvailableAsync_UsesGivenFieldAndStatusCode()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);

            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureHostsAvailableAsync(
                    new[] { "shop.example.jp" }, CancellationToken.None, statusCode: 409, field: "allowed_rp_ids"));

            Assert.Equal(409, ex.StatusCode);
            Assert.Equal("allowed_rp_ids", ex.Field);
        }

        [Fact]
        public async Task EnsureHostsAvailableAsync_LikeWildcardInHost_DoesNotWidenMatch()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            await SeedSiteAsync(context, 1, "shop-example-jp", new[] { "shop.example.jp" });

            var service = CreateService(context);

            // "_" や "%" を含む文字列がホストとして渡されても LIKE の部分一致に化けない
            // （NormalizeRpId が通す値ではないが、プリフィルタの安全性として固定する）。
            await service.EnsureHostsAvailableAsync(new[] { "shop_example.jp" }, CancellationToken.None);
            await service.EnsureHostsAvailableAsync(new[] { "%.example.jp" }, CancellationToken.None);
        }

        [Fact]
        public async Task ProvisionAsync_CreatesOrganizationClientKeyPairAndMembership()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp/subdir/", isSandbox: false, "site_url");

            var provisioned = await service.ProvisionAsync(
                site, "Shop Inc.", "4", "account-subject", parentOrganizationId: null);

            Assert.Equal("shop-example-jp", provisioned.Organization.Code);
            Assert.Equal("shop-example-jp", provisioned.Organization.TenantName);
            Assert.Null(provisioned.Organization.ParentOrganizationId);

            var client = await context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.RedirectUris)
                .FirstAsync(c => c.OrganizationId == provisioned.Organization.Id);
            // サブディレクトリインストールのベースパスを引き継ぐ。
            Assert.Equal("https://shop.example.jp/subdir/ecauth/callback", client.RedirectUris!.Single().Uri);
            Assert.StartsWith("ec-shop-example-jp-", client.ClientId);

            Assert.True(await context.RsaKeyPairs.IgnoreQueryFilters()
                .AnyAsync(k => k.OrganizationId == provisioned.Organization.Id));
            Assert.True(await context.AccountOrganizations.IgnoreQueryFilters()
                .AnyAsync(ao => ao.OrganizationId == provisioned.Organization.Id
                    && ao.AccountSubject == "account-subject"
                    && ao.Role == "owner"));
        }

        [Fact]
        public async Task ProvisionAsync_Sandbox_LinksToParentOrganization()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            var production = await service.ProvisionAsync(
                service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url"),
                "Shop Inc.", "4", "account-subject", parentOrganizationId: null);

            var sandbox = await service.ProvisionAsync(
                service.BuildSite("https://stg.example.jp", isSandbox: true, "site_url"),
                "Shop Inc.", "4", "account-subject", parentOrganizationId: production.Organization.Id);

            // 別ドメインのテストサイトでも本番に紐づく（組織コードからは導出できない関係）。
            Assert.Equal("stg-example-jp-sandbox", sandbox.Organization.Code);
            Assert.Equal(production.Organization.Id, sandbox.Organization.ParentOrganizationId);
        }
        // ---- 既存 Organization への Client 追加（EcAuthDocs#121 項目 3）----

        [Fact]
        public async Task AddClientAsync_CreatesClientWithSameRulesAsFirstClient()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            var provisioned = await service.ProvisionAsync(
                service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url"),
                "Shop Inc.", "4", "account-subject", parentOrganizationId: null);

            // 追加する Client のホストは別（WordPress を同じ組織にぶら下げる）。
            var site = service.BuildSite("https://www.wp.example.jp/blog/index.php", isSandbox: false, "site_url");
            var added = await service.AddClientAsync(provisioned.Organization, site, "WordPress", "2");

            var stored = await context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.RedirectUris)
                .FirstAsync(c => c.Id == added.Id);

            // Organization は据え置き（組織コード・テナント名は最初のサイト由来のまま）。
            Assert.Equal(provisioned.Organization.Id, stored.OrganizationId);
            Assert.Equal("shop-example-jp", provisioned.Organization.Code);
            // client_id は追加先 Organization の組織コードから導出する（site.Code は使わない）。
            Assert.StartsWith("ec-shop-example-jp-", stored.ClientId);
            Assert.NotEqual(provisioned.Client.ClientId, stored.ClientId);
            Assert.Equal(SubjectType.B2B, stored.SubjectType);
            Assert.Equal("WordPress", stored.AppName);
            // 初期 redirect_uri / allowed_rp_ids は最初の Client と同じ規則（ベースパス継承・www. 除去版）。
            Assert.Equal("https://www.wp.example.jp/blog/ecauth/callback.php", stored.RedirectUris!.Single().Uri);
            Assert.Equal(new[] { "www.wp.example.jp", "wp.example.jp" }, stored.AllowedRpIds);
            Assert.False(string.IsNullOrEmpty(stored.ClientSecret));

            // RsaKeyPair / AccountOrganization は増えない（Organization 単位のまま）。
            Assert.Equal(1, await context.RsaKeyPairs.IgnoreQueryFilters()
                .CountAsync(k => k.OrganizationId == provisioned.Organization.Id));
            Assert.Equal(1, await context.AccountOrganizations.IgnoreQueryFilters()
                .CountAsync(ao => ao.OrganizationId == provisioned.Organization.Id));
            Assert.Equal(2, await context.Clients.IgnoreQueryFilters()
                .CountAsync(c => c.OrganizationId == provisioned.Organization.Id));
        }

        [Fact]
        public async Task AddClientAsync_SecondClientHostIsThenOccupiedForOtherOrganizations()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = CreateService(context);

            var provisioned = await service.ProvisionAsync(
                service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url"),
                "Shop Inc.", "4", "account-subject", parentOrganizationId: null);
            await service.AddClientAsync(
                provisioned.Organization,
                service.BuildSite("https://wp.example.jp", isSandbox: false, "site_url"),
                "WordPress", "4");

            // 別アカウントの申込（管理下 Org なし）は wp.example.jp を取れない。
            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(
                    new[] { service.BuildSite("https://wp.example.jp", isSandbox: false, "site_url") },
                    CancellationToken.None));
            Assert.Equal("organization_already_exists", ex.Error);

            // 同じ組織の管理者は同じホストでさらに Client を足せる。
            await service.EnsureHostsAvailableAsync(
                new[] { "wp.example.jp" }, CancellationToken.None,
                ownedOrganizationIds: new[] { provisioned.Organization.Id });
        }
    }
}
