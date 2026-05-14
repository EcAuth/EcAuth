using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Test.Models
{
    /// <summary>
    /// Account 管理機能の DbContext 配線確認。
    /// テナントフィルター / Alternate Key / Unique Index / Client.SubjectType 等を網羅。
    /// </summary>
    public class AccountManagementDbContextTests
    {
        [Fact]
        public async Task Account_QueryFilter_AppliesByTenant()
        {
            // accounts と stg-accounts の 2 つの Org に Account を投入し、
            // tenant=accounts で SELECT すると accounts 配下の Account のみ取れることを確認
            var dbName = Guid.NewGuid().ToString();

            // tenant=accounts で投入
            var accountsTenant = new MockTenantService();
            accountsTenant.SetTenant("accounts");
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, accountsTenant))
            {
                var accountsOrg = new Organization { Code = "accounts", Name = "Accounts", TenantName = "accounts" };
                var stgOrg = new Organization { Code = "stg-accounts", Name = "Stg Accounts", TenantName = "stg-accounts" };
                ctx.Organizations.AddRange(accountsOrg, stgOrg);
                await ctx.SaveChangesAsync();

                ctx.Accounts.Add(new Account
                {
                    Subject = Guid.NewGuid().ToString(),
                    Email = "prod@example.jp",
                    OrganizationId = accountsOrg.Id,
                    Organization = accountsOrg
                });
                ctx.Accounts.Add(new Account
                {
                    Subject = Guid.NewGuid().ToString(),
                    Email = "stg@example.jp",
                    OrganizationId = stgOrg.Id,
                    Organization = stgOrg
                });
                await ctx.SaveChangesAsync();
            }

            // tenant=accounts で SELECT
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, accountsTenant))
            {
                var visible = await ctx.Accounts.Include(a => a.Organization).ToListAsync();
                Assert.Single(visible);
                Assert.Equal("prod@example.jp", visible[0].Email);
            }

            // tenant=stg-accounts で SELECT
            var stgTenant = new MockTenantService();
            stgTenant.SetTenant("stg-accounts");
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, stgTenant))
            {
                var visible = await ctx.Accounts.Include(a => a.Organization).ToListAsync();
                Assert.Single(visible);
                Assert.Equal("stg@example.jp", visible[0].Email);
            }

            // IgnoreQueryFilters で両方見える
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, accountsTenant))
            {
                var all = await ctx.Accounts.IgnoreQueryFilters().ToListAsync();
                Assert.Equal(2, all.Count);
            }
        }

        [Fact]
        public void Account_HasAlternateKeyOnSubject()
        {
            using var ctx = TestDbContextHelper.CreateInMemoryContext();
            var entityType = ctx.Model.FindEntityType(typeof(Account));
            var alternateKey = entityType?.GetKeys()
                .FirstOrDefault(k => !k.IsPrimaryKey()
                    && k.Properties.Count == 1
                    && k.Properties[0].Name == nameof(Account.Subject));

            Assert.NotNull(alternateKey);
        }

        [Fact]
        public void Account_HasUniqueIndexOnOrganizationIdAndEmail()
        {
            using var ctx = TestDbContextHelper.CreateInMemoryContext();
            var entityType = ctx.Model.FindEntityType(typeof(Account));
            var index = entityType?.GetIndexes()
                .FirstOrDefault(i => i.Properties.Count == 2
                    && i.Properties[0].Name == nameof(Account.OrganizationId)
                    && i.Properties[1].Name == nameof(Account.Email));

            Assert.NotNull(index);
            Assert.True(index.IsUnique);
        }

        [Fact]
        public async Task AccountOrganization_IsCrossTenantVisible()
        {
            // 異なるテナントの Organization に Account から AccountOrganization を張れることを確認
            var dbName = Guid.NewGuid().ToString();
            var tenant = new MockTenantService();
            tenant.SetTenant("accounts");

            int customerOrgId;
            string subject;
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, tenant))
            {
                var accountsOrg = new Organization { Code = "accounts", Name = "A", TenantName = "accounts" };
                var customerOrg = new Organization { Code = "example-jp", Name = "C", TenantName = "example-jp" };
                ctx.Organizations.AddRange(accountsOrg, customerOrg);
                await ctx.SaveChangesAsync();

                subject = Guid.NewGuid().ToString();
                ctx.Accounts.Add(new Account
                {
                    Subject = subject,
                    Email = "x@example.jp",
                    OrganizationId = accountsOrg.Id,
                    Organization = accountsOrg
                });
                ctx.AccountOrganizations.Add(new AccountOrganization
                {
                    AccountSubject = subject,
                    OrganizationId = customerOrg.Id
                });
                await ctx.SaveChangesAsync();
                customerOrgId = customerOrg.Id;
            }

            // 別テナント (example-jp) でも AccountOrganization は直接見える
            var customerTenant = new MockTenantService();
            customerTenant.SetTenant("example-jp");
            using (var ctx = TestDbContextHelper.CreateInMemoryContext(dbName, customerTenant))
            {
                var ao = await ctx.AccountOrganizations
                    .FirstOrDefaultAsync(x => x.OrganizationId == customerOrgId);
                Assert.NotNull(ao);
                Assert.Equal(subject, ao.AccountSubject);
            }
        }

        [Fact]
        public void Client_SubjectType_DefaultValue_IsB2C()
        {
            var client = new Client();
            Assert.Equal(SubjectType.B2C, client.SubjectType);
        }

        [Fact]
        public async Task Client_SubjectType_CanBeSetAndPersisted()
        {
            using var ctx = TestDbContextHelper.CreateInMemoryContext();
            var organization = new Organization { Code = "accounts", Name = "A", TenantName = "accounts" };
            ctx.Organizations.Add(organization);
            await ctx.SaveChangesAsync();

            ctx.Clients.Add(new Client
            {
                ClientId = "ecauth-admin-console",
                ClientSecret = "secret",
                AppName = "Admin Console",
                OrganizationId = organization.Id,
                SubjectType = SubjectType.Account
            });
            await ctx.SaveChangesAsync();

            var saved = await ctx.Clients.FirstOrDefaultAsync(c => c.ClientId == "ecauth-admin-console");
            Assert.NotNull(saved);
            Assert.Equal(SubjectType.Account, saved.SubjectType);
        }
    }
}
