using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Test.Models
{
    public class AccountOrganizationTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly MockTenantService _tenantService;

        public AccountOrganizationTests()
        {
            _tenantService = new MockTenantService();
            _tenantService.SetTenant("accounts");
            _context = TestDbContextHelper.CreateInMemoryContext(tenantService: _tenantService);
        }

        [Fact]
        public void AccountOrganization_DefaultValues_ShouldBeSetCorrectly()
        {
            var ao = new AccountOrganization();

            Assert.Equal(string.Empty, ao.AccountSubject);
            Assert.Equal(0, ao.OrganizationId);
            Assert.Equal("owner", ao.Role);
            Assert.True(ao.CreatedAt <= DateTimeOffset.UtcNow);
        }

        [Fact]
        public async Task AccountOrganization_WithAccountAndOrganization_ShouldSaveCorrectly()
        {
            var accountsOrg = new Organization { Code = "accounts", Name = "Accounts", TenantName = "accounts" };
            var customerOrg = new Organization { Code = "example-jp", Name = "Example", TenantName = "example-jp" };
            _context.Organizations.AddRange(accountsOrg, customerOrg);
            await _context.SaveChangesAsync();

            var subject = Guid.NewGuid().ToString();
            var account = new Account
            {
                Subject = subject,
                Email = "owner@example.jp",
                OrganizationId = accountsOrg.Id,
                Organization = accountsOrg
            };
            _context.Accounts.Add(account);

            var ao = new AccountOrganization
            {
                AccountSubject = subject,
                OrganizationId = customerOrg.Id,
                Role = "owner",
                Account = account,
                Organization = customerOrg
            };
            _context.AccountOrganizations.Add(ao);
            await _context.SaveChangesAsync();

            // 本番コードでも AccountOrganization はテナント横断アクセスのため
            // IgnoreQueryFilters で取得する想定（required リレーションの自動継承フィルター回避）
            var saved = await _context.AccountOrganizations
                .IgnoreQueryFilters()
                .Include(x => x.Account)
                .Include(x => x.Organization)
                .FirstOrDefaultAsync(x => x.AccountSubject == subject);

            Assert.NotNull(saved);
            Assert.Equal("owner", saved.Role);
            Assert.NotNull(saved.Account);
            Assert.NotNull(saved.Organization);
            Assert.Equal("example-jp", saved.Organization.Code);
        }

        [Fact]
        public void AccountOrganization_HasNoQueryFilter_ForCrossTenantAccess()
        {
            var entityType = _context.Model.FindEntityType(typeof(AccountOrganization));

            Assert.NotNull(entityType);
            var queryFilter = entityType.GetQueryFilter();
            Assert.Null(queryFilter);
        }

        [Fact]
        public void AccountOrganization_HasCompositePrimaryKey()
        {
            var entityType = _context.Model.FindEntityType(typeof(AccountOrganization));
            var pk = entityType?.FindPrimaryKey();

            Assert.NotNull(pk);
            Assert.Equal(2, pk.Properties.Count);
            Assert.Contains(pk.Properties, p => p.Name == nameof(AccountOrganization.AccountSubject));
            Assert.Contains(pk.Properties, p => p.Name == nameof(AccountOrganization.OrganizationId));
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
