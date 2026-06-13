using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IdentityProvider.Test.Services
{
    public class AccountServiceTests
    {
        private readonly ILogger<AccountService> _logger;

        public AccountServiceTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<AccountService>();
        }

        [Fact]
        public async Task GetBySubjectAsync_ExistingAccount_ReturnsAccount()
        {
            // Arrange - Account が所属するテナント（accounts）を解決した状態を再現
            var tenantService = new MockTenantService();
            tenantService.SetTenant("accounts");
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);

            var accountsOrg = new Organization { Id = 1, Code = "accounts", Name = "EcAuth Accounts", TenantName = "accounts" };
            context.Organizations.Add(accountsOrg);
            context.Accounts.Add(new Account
            {
                Id = 1,
                Subject = "account-subject",
                Email = "owner@example.com",
                OrganizationId = 1
            });
            await context.SaveChangesAsync();

            var service = new AccountService(context, _logger);

            // Act
            var account = await service.GetBySubjectAsync("account-subject");

            // Assert
            Assert.NotNull(account);
            Assert.Equal("owner@example.com", account!.Email);
        }

        [Fact]
        public async Task GetBySubjectAsync_UnknownSubject_ReturnsNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new AccountService(context, _logger);

            var account = await service.GetBySubjectAsync("nonexistent");

            Assert.Null(account);
        }

        [Fact]
        public async Task GetManagedOrganizationsAsync_ReturnsCrossTenantOrgsWithCodeAndRole()
        {
            // Arrange - 管理対象は別テナント（顧客 Org）。IgnoreQueryFilters で横断取得できることを確認
            var tenantService = new MockTenantService();
            tenantService.SetTenant("accounts");
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);

            var customerA = new Organization { Id = 10, Code = "customer-shop", Name = "Customer Shop", TenantName = "customer-shop" };
            var customerB = new Organization { Id = 11, Code = "another-shop", Name = "Another Shop", TenantName = "another-shop" };
            context.Organizations.AddRange(customerA, customerB);

            context.AccountOrganizations.AddRange(
                new AccountOrganization { AccountSubject = "account-subject", OrganizationId = 10, Role = "owner" },
                new AccountOrganization { AccountSubject = "account-subject", OrganizationId = 11, Role = "admin" });
            await context.SaveChangesAsync();

            var service = new AccountService(context, _logger);

            // Act
            var managed = await service.GetManagedOrganizationsAsync("account-subject");

            // Assert
            Assert.Equal(2, managed.Count);
            var owner = Assert.Single(managed, m => m.OrganizationId == 10);
            Assert.Equal("customer-shop", owner.Code);
            Assert.Equal("owner", owner.Role);
            var admin = Assert.Single(managed, m => m.OrganizationId == 11);
            Assert.Equal("another-shop", admin.Code);
            Assert.Equal("admin", admin.Role);
        }

        [Fact]
        public async Task GetManagedOrganizationsAsync_NoManagedOrgs_ReturnsEmpty()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new AccountService(context, _logger);

            var managed = await service.GetManagedOrganizationsAsync("account-subject");

            Assert.Empty(managed);
        }
    }
}
