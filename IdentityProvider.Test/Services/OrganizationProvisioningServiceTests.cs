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

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_DomainOwnedByAnotherAccount_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "shop-example-jp",
                Name = "Shop",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

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
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "shop-example-jp",
                Name = "Shop",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: true, "site_url");

            // 自分の本番 Org と同じドメイン。組織コードは -sandbox で分かれるので通る。
            await service.EnsureOrganizationCodesAvailableAsync(
                new[] { site }, CancellationToken.None, ownedOrganizationIds: new[] { 1 });
        }

        [Fact]
        public async Task EnsureOrganizationCodesAvailableAsync_SameCodeAsOwnOrganization_Throws()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "shop-example-jp",
                Name = "Shop",
                TenantName = "shop-example-jp"
            });
            await context.SaveChangesAsync();

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
            context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "shop-example-jp",
                Name = "Shop",
                TenantName = "shop-example-jp",
                DeletedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();

            var service = CreateService(context);
            var site = service.BuildSite("https://shop.example.jp", isSandbox: false, "site_url");

            // 論理削除しても組織コードは解放しない（課金集計で期間が混ざるため）。
            // 「他人が使っている」のとは理由が違うので、エラーコードで区別する。
            var ex = await Assert.ThrowsAsync<SignupValidationException>(
                () => service.EnsureOrganizationCodesAvailableAsync(
                    new[] { site }, CancellationToken.None, ownedOrganizationIds: new[] { 1 }));

            Assert.Equal("organization_deleted", ex.Error);
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
    }
}
