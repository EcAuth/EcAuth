using IdentityProvider.Data.Seeders;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Data.Seeders
{
    public class AccountsOrganizationSeederTests : IDisposable
    {
        #region Test Constants

        private const string AccountsClientId = "ecauth-admin-console";
        private const string AccountsClientSecret = "accounts-secret";
        private const string AccountsAllowedRpIds = "accounts.ec-auth.io";
        private const string AccountsRedirectUri = "https://accounts.ec-auth.io/auth/callback";

        private const string StgAccountsClientId = "ecauth-admin-console-stg";
        private const string StgAccountsClientSecret = "stg-accounts-secret";
        private const string StgAccountsAllowedRpIds = "stg-accounts.ec-auth.io";
        private const string StgAccountsRedirectUri = "https://stg-accounts.ec-auth.io/auth/callback";

        #endregion

        private readonly EcAuthDbContext _context;
        private readonly AccountsOrganizationSeeder _seeder;
        private readonly Mock<ILogger> _mockLogger;

        public AccountsOrganizationSeederTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _seeder = new AccountsOrganizationSeeder();
            _mockLogger = new Mock<ILogger>();
        }

        #region Metadata Tests

        [Fact]
        public void RequiredMigration_ShouldBeAccountManagementSchema()
        {
            Assert.Equal("20260514085625_AddAccountManagementSchema", _seeder.RequiredMigration);
        }

        [Fact]
        public void Order_ShouldBe20()
        {
            Assert.Equal(20, _seeder.Order);
        }

        #endregion

        #region Organization Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateBothOrganizations()
        {
            // Arrange
            var configuration = CreateFullConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var accounts = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == "accounts");
            Assert.NotNull(accounts);
            Assert.Equal("accounts", accounts.TenantName);

            var stgAccounts = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == "stg-accounts");
            Assert.NotNull(stgAccounts);
            Assert.Equal("stg-accounts", stgAccounts.TenantName);
        }

        #endregion

        #region Client Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateClientsWithSubjectTypeAccount()
        {
            // Arrange
            var configuration = CreateFullConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var accountsClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ClientId == AccountsClientId);
            Assert.NotNull(accountsClient);
            Assert.Equal(SubjectType.Account, accountsClient.SubjectType);
            Assert.Equal(AccountsClientSecret, accountsClient.ClientSecret);
            Assert.Contains(AccountsAllowedRpIds, accountsClient.AllowedRpIds);

            var stgClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ClientId == StgAccountsClientId);
            Assert.NotNull(stgClient);
            Assert.Equal(SubjectType.Account, stgClient.SubjectType);
            Assert.Contains(StgAccountsAllowedRpIds, stgClient.AllowedRpIds);
        }

        [Fact]
        public async Task SeedAsync_WithoutClientSecret_ShouldCreateOrganizationButSkipClient()
        {
            // Arrange - accounts のみ、CLIENT_SECRET 未設定
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ACCOUNTS_CLIENT_ID"] = AccountsClientId
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var orgExists = await _context.Organizations
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Code == "accounts");
            Assert.True(orgExists);

            var clientCount = await _context.Clients.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, clientCount);
        }

        #endregion

        #region RedirectUri / RsaKeyPair Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateRedirectUriAndRsaKeyPairPerOrganization()
        {
            // Arrange
            var configuration = CreateFullConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert - RedirectUri
            var accountsClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == AccountsClientId);
            var redirectExists = await _context.RedirectUris
                .IgnoreQueryFilters()
                .AnyAsync(r => r.Uri == AccountsRedirectUri && r.ClientId == accountsClient.Id);
            Assert.True(redirectExists);

            // Assert - RsaKeyPair が各 Org に 1 件ずつ
            var accounts = await _context.Organizations
                .IgnoreQueryFilters().FirstAsync(o => o.Code == "accounts");
            var stgAccounts = await _context.Organizations
                .IgnoreQueryFilters().FirstAsync(o => o.Code == "stg-accounts");

            Assert.Equal(1, await _context.RsaKeyPairs
                .IgnoreQueryFilters().CountAsync(r => r.OrganizationId == accounts.Id));
            Assert.Equal(1, await _context.RsaKeyPairs
                .IgnoreQueryFilters().CountAsync(r => r.OrganizationId == stgAccounts.Id));
        }

        #endregion

        #region Skip Conditions Tests

        [Fact]
        public async Task SeedAsync_WithNoConfiguration_ShouldSeedNothing()
        {
            // Arrange - staging 等、ACCOUNTS_* / STG_ACCOUNTS_* 未設定の環境
            var configuration = CreateConfiguration(new Dictionary<string, string?>());

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            Assert.Equal(0, await _context.Organizations.IgnoreQueryFilters().CountAsync());
            Assert.Equal(0, await _context.Clients.IgnoreQueryFilters().CountAsync());
        }

        [Fact]
        public async Task SeedAsync_WithOnlyAccountsConfigured_ShouldSkipStgAccounts()
        {
            // Arrange - accounts のみ設定（dev で片方だけ検証するケース）
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ACCOUNTS_CLIENT_ID"] = AccountsClientId,
                ["ACCOUNTS_CLIENT_SECRET"] = AccountsClientSecret
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            Assert.True(await _context.Organizations
                .IgnoreQueryFilters().AnyAsync(o => o.Code == "accounts"));
            Assert.False(await _context.Organizations
                .IgnoreQueryFilters().AnyAsync(o => o.Code == "stg-accounts"));
        }

        #endregion

        #region Idempotency Tests

        [Fact]
        public async Task SeedAsync_CalledMultipleTimes_ShouldBeIdempotent()
        {
            // Arrange
            var configuration = CreateFullConfiguration();

            // Act - 3 回実行
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            Assert.Equal(2, await _context.Organizations.IgnoreQueryFilters().CountAsync());
            Assert.Equal(2, await _context.Clients.IgnoreQueryFilters().CountAsync());
            Assert.Equal(2, await _context.RsaKeyPairs.IgnoreQueryFilters().CountAsync());
            Assert.Equal(2, await _context.RedirectUris.IgnoreQueryFilters().CountAsync());
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }

        #region Helper Methods

        private static IConfiguration CreateFullConfiguration()
        {
            return CreateConfiguration(new Dictionary<string, string?>
            {
                ["ACCOUNTS_CLIENT_ID"] = AccountsClientId,
                ["ACCOUNTS_CLIENT_SECRET"] = AccountsClientSecret,
                ["ACCOUNTS_ALLOWED_RP_IDS"] = AccountsAllowedRpIds,
                ["ACCOUNTS_REDIRECT_URI"] = AccountsRedirectUri,
                ["STG_ACCOUNTS_CLIENT_ID"] = StgAccountsClientId,
                ["STG_ACCOUNTS_CLIENT_SECRET"] = StgAccountsClientSecret,
                ["STG_ACCOUNTS_ALLOWED_RP_IDS"] = StgAccountsAllowedRpIds,
                ["STG_ACCOUNTS_REDIRECT_URI"] = StgAccountsRedirectUri
            });
        }

        private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        #endregion
    }
}
