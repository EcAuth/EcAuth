using IdentityProvider.Data.Seeders;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Data.Seeders
{
    public class OrganizationClientSeederTests : IDisposable
    {
        #region Test Constants

        private const string TestOrganizationCode = "test-org";
        private const string TestOrganizationName = "Test Organization";
        private const string TestTenantName = "test-tenant";
        private const string TestClientId = "test-client-id";
        private const string TestClientSecret = "test-client-secret";
        private const string TestAppName = "Test App";
        private const string TestRedirectUri = "https://localhost:8081/v1/auth/callback";
        private const string TestMockIdpAppName = "test-mock-idp";
        private const string TestMockIdpClientId = "mock-idp-client-id";
        private const string TestMockIdpClientSecret = "mock-idp-client-secret";
        private const string TestMockIdpAuthEndpoint = "https://mock-idp.example.com/authorize";
        private const string TestMockIdpTokenEndpoint = "https://mock-idp.example.com/token";
        private const string TestMockIdpUserinfoEndpoint = "https://mock-idp.example.com/userinfo";

        #endregion

        private readonly EcAuthDbContext _context;
        private readonly OrganizationClientSeeder _seeder;
        private readonly Mock<ILogger> _mockLogger;

        public OrganizationClientSeederTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _seeder = new OrganizationClientSeeder();
            _mockLogger = new Mock<ILogger>();
        }

        #region RequiredMigration Tests

        [Fact]
        public void RequiredMigration_ShouldBeCorrectMigrationName()
        {
            Assert.Equal("20260328225834_AddKidAndIsActiveToRsaKeyPair", _seeder.RequiredMigration);
        }

        [Fact]
        public void Order_ShouldBe10()
        {
            Assert.Equal(10, _seeder.Order);
        }

        #endregion

        #region Organization Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateOrganization()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var org = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == TestOrganizationCode);

            Assert.NotNull(org);
            Assert.Equal(TestOrganizationName, org.Name);
            Assert.Equal(TestTenantName, org.TenantName);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateOrganization()
        {
            // Arrange
            _context.Organizations.Add(new Organization
            {
                Code = TestOrganizationCode,
                Name = "Existing Org",
                TenantName = TestTenantName
            });
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var count = await _context.Organizations
                .IgnoreQueryFilters()
                .CountAsync(o => o.Code == TestOrganizationCode);

            Assert.Equal(1, count);

            // Name は更新されないことを確認
            var org = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.Code == TestOrganizationCode);
            Assert.Equal("Existing Org", org.Name);
        }

        #endregion

        #region Client Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateClient()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ClientId == TestClientId);

            Assert.NotNull(client);
            Assert.Equal(TestClientSecret, client.ClientSecret);
            Assert.Equal(TestAppName, client.AppName);
            Assert.NotNull(client.OrganizationId);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateClient()
        {
            // Arrange
            var org = new Organization
            {
                Code = TestOrganizationCode,
                Name = TestOrganizationName,
                TenantName = TestTenantName
            };
            _context.Organizations.Add(org);
            await _context.SaveChangesAsync();

            _context.Clients.Add(new Client
            {
                ClientId = TestClientId,
                ClientSecret = "existing-secret",
                AppName = "Existing App",
                OrganizationId = org.Id
            });
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var count = await _context.Clients
                .IgnoreQueryFilters()
                .CountAsync(c => c.ClientId == TestClientId);

            Assert.Equal(1, count);

            // ClientSecret は更新されないことを確認
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);
            Assert.Equal("existing-secret", client.ClientSecret);
        }

        #endregion

        #region RedirectUri Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateRedirectUri()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var exists = await _context.RedirectUris
                .IgnoreQueryFilters()
                .AnyAsync(r => r.Uri == TestRedirectUri && r.ClientId == client.Id);

            Assert.True(exists);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateRedirectUri()
        {
            // Arrange - 1回目のシードで作成
            var configuration = CreateDevConfiguration();
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Act - 2回目のシード
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var count = await _context.RedirectUris
                .IgnoreQueryFilters()
                .CountAsync(r => r.Uri == TestRedirectUri && r.ClientId == client.Id);

            Assert.Equal(1, count);
        }

        #endregion

        #region RsaKeyPair Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateRsaKeyPair()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var organization = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.Code == TestOrganizationCode);

            var rsaKeyPair = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.OrganizationId == organization.Id);

            Assert.NotNull(rsaKeyPair);
            Assert.NotEmpty(rsaKeyPair.PublicKey);
            Assert.NotEmpty(rsaKeyPair.PrivateKey);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateRsaKeyPair()
        {
            // Arrange - 1回目のシードで作成
            var configuration = CreateDevConfiguration();
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            var organization = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.Code == TestOrganizationCode);

            var originalKeyPair = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .FirstAsync(r => r.OrganizationId == organization.Id);

            var originalPublicKey = originalKeyPair.PublicKey;

            // Act - 2回目のシード
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var count = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .CountAsync(r => r.OrganizationId == organization.Id);

            Assert.Equal(1, count);

            // 公開鍵は変更されていないことを確認
            var keyPair = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .FirstAsync(r => r.OrganizationId == organization.Id);
            Assert.Equal(originalPublicKey, keyPair.PublicKey);
        }

        #endregion

        #region OpenIdProvider Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateOpenIdProvider()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var provider = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Name == TestMockIdpAppName && o.ClientId == client.Id);

            Assert.NotNull(provider);
            Assert.Equal(TestMockIdpClientId, provider.IdpClientId);
            Assert.Equal(TestMockIdpClientSecret, provider.IdpClientSecret);
            Assert.Equal(TestMockIdpAuthEndpoint, provider.AuthorizationEndpoint);
            Assert.Equal(TestMockIdpTokenEndpoint, provider.TokenEndpoint);
            Assert.Equal(TestMockIdpUserinfoEndpoint, provider.UserinfoEndpoint);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateOpenIdProvider()
        {
            // Arrange - 1回目のシードで作成
            var configuration = CreateDevConfiguration();
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Act - 2回目のシード
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var count = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .CountAsync(o => o.Name == TestMockIdpAppName && o.ClientId == client.Id);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task SeedAsync_ShouldSkipOpenIdProvider_WhenMockIdpNotConfigured()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEFAULT_MOCK_IDP_APP_NAME"] = null,
                ["DEFAULT_MOCK_IDP_CLIENT_ID"] = null
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var providerCount = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, providerCount);

            // Organization と Client は作成されていること
            var orgExists = await _context.Organizations
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Code == TestOrganizationCode);
            Assert.True(orgExists);

            var clientExists = await _context.Clients
                .IgnoreQueryFilters()
                .AnyAsync(c => c.ClientId == TestClientId);
            Assert.True(clientExists);
        }

        #endregion

        #region Environment Prefix Tests

        [Theory]
        [InlineData("Development", "DEFAULT")]
        [InlineData("Staging", "STAGING")]
        [InlineData("Production", "PROD")]
        public async Task SeedAsync_ShouldUseCorrectEnvironmentPrefix(string environment, string expectedPrefix)
        {
            // Arrange
            var orgCode = $"{expectedPrefix.ToLower()}-org";
            var configValues = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                [$"{expectedPrefix}_ORGANIZATION_CODE"] = orgCode,
                [$"{expectedPrefix}_ORGANIZATION_NAME"] = $"{expectedPrefix} Org",
                [$"{expectedPrefix}_ORGANIZATION_TENANT_NAME"] = $"{expectedPrefix.ToLower()}-tenant",
                [$"{expectedPrefix}_CLIENT_ID"] = $"{expectedPrefix.ToLower()}-client",
                [$"{expectedPrefix}_CLIENT_SECRET"] = $"{expectedPrefix.ToLower()}-secret",
                [$"{expectedPrefix}_APP_NAME"] = $"{expectedPrefix} App",
            };

            var configuration = CreateConfiguration(configValues);

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var org = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == orgCode);

            Assert.NotNull(org);
        }

        [Fact]
        public async Task SeedAsync_UnknownEnvironment_ShouldDefaultToDev()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Unknown",
                ["DEFAULT_ORGANIZATION_CODE"] = "dev-fallback-org",
                ["DEFAULT_ORGANIZATION_NAME"] = "Dev Fallback",
                ["DEFAULT_CLIENT_ID"] = "dev-fallback-client",
                ["DEFAULT_CLIENT_SECRET"] = "dev-fallback-secret"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var org = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Code == "dev-fallback-org");

            Assert.NotNull(org);
        }

        #endregion

        #region Skip Conditions Tests

        [Fact]
        public async Task SeedAsync_WithoutOrganizationCode_ShouldSkip()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var orgCount = await _context.Organizations.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, orgCount);
        }

        [Fact]
        public async Task SeedAsync_WithoutClientId_ShouldSkip()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var clientCount = await _context.Clients.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, clientCount);
        }

        [Fact]
        public async Task SeedAsync_WithoutClientSecret_ShouldSkipClientAndDependents()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode,
                ["DEFAULT_CLIENT_ID"] = TestClientId
                // DEFAULT_CLIENT_SECRET is not set
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            // Organization は作成される
            var orgExists = await _context.Organizations
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Code == TestOrganizationCode);
            Assert.True(orgExists);

            // Client は作成されない
            var clientCount = await _context.Clients.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, clientCount);
        }

        [Fact]
        public async Task SeedAsync_WithoutRedirectUri_ShouldSkipRedirectUri()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEFAULT_REDIRECT_URI"] = null
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var redirectUriCount = await _context.RedirectUris.IgnoreQueryFilters().CountAsync();
            Assert.Equal(0, redirectUriCount);

            // Client は作成されること
            var clientExists = await _context.Clients
                .IgnoreQueryFilters()
                .AnyAsync(c => c.ClientId == TestClientId);
            Assert.True(clientExists);
        }

        #endregion

        #region Idempotency Tests

        [Fact]
        public async Task SeedAsync_CalledMultipleTimes_ShouldBeIdempotent()
        {
            // Arrange
            var configuration = CreateDevConfiguration();

            // Act - 3回実行
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var orgCount = await _context.Organizations
                .IgnoreQueryFilters()
                .CountAsync(o => o.Code == TestOrganizationCode);
            Assert.Equal(1, orgCount);

            var clientCount = await _context.Clients
                .IgnoreQueryFilters()
                .CountAsync(c => c.ClientId == TestClientId);
            Assert.Equal(1, clientCount);

            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            var redirectUriCount = await _context.RedirectUris
                .IgnoreQueryFilters()
                .CountAsync(r => r.Uri == TestRedirectUri && r.ClientId == client.Id);
            Assert.Equal(1, redirectUriCount);

            var organization = await _context.Organizations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.Code == TestOrganizationCode);

            var rsaKeyPairCount = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .CountAsync(r => r.OrganizationId == organization.Id);
            Assert.Equal(1, rsaKeyPairCount);

            var providerCount = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .CountAsync(o => o.Name == TestMockIdpAppName && o.ClientId == client.Id);
            Assert.Equal(1, providerCount);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }

        #region Helper Methods

        private static IConfiguration CreateDevConfiguration(Dictionary<string, string?>? overrides = null)
        {
            var baseValues = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode,
                ["DEFAULT_ORGANIZATION_NAME"] = TestOrganizationName,
                ["DEFAULT_ORGANIZATION_TENANT_NAME"] = TestTenantName,
                ["DEFAULT_CLIENT_ID"] = TestClientId,
                ["DEFAULT_CLIENT_SECRET"] = TestClientSecret,
                ["DEFAULT_APP_NAME"] = TestAppName,
                ["DEFAULT_REDIRECT_URI"] = TestRedirectUri,
                ["DEFAULT_MOCK_IDP_APP_NAME"] = TestMockIdpAppName,
                ["DEFAULT_MOCK_IDP_CLIENT_ID"] = TestMockIdpClientId,
                ["DEFAULT_MOCK_IDP_CLIENT_SECRET"] = TestMockIdpClientSecret,
                ["DEFAULT_MOCK_IDP_AUTHORIZATION_ENDPOINT"] = TestMockIdpAuthEndpoint,
                ["DEFAULT_MOCK_IDP_TOKEN_ENDPOINT"] = TestMockIdpTokenEndpoint,
                ["DEFAULT_MOCK_IDP_USERINFO_ENDPOINT"] = TestMockIdpUserinfoEndpoint
            };

            if (overrides != null)
            {
                foreach (var kvp in overrides)
                {
                    baseValues[kvp.Key] = kvp.Value;
                }
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(baseValues)
                .Build();
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
