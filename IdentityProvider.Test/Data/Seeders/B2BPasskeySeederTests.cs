using IdentityProvider.Data.Seeders;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Data.Seeders
{
    public class B2BPasskeySeederTests : IDisposable
    {
        #region Test Constants

        private const string TestClientId = "test-client-id";
        private const string TestOrganizationCode = "test-org";
        private const string TestTenantName = "test-tenant";
        private const string TestAppName = "Test App";

        #endregion

        private readonly EcAuthDbContext _context;
        private readonly B2BPasskeySeeder _seeder;
        private readonly Mock<ILogger> _mockLogger;
        private readonly Organization _organization;
        private readonly Client _client;

        public B2BPasskeySeederTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _seeder = new B2BPasskeySeeder();
            _mockLogger = new Mock<ILogger>();

            // テスト用の Organization をセットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = TestOrganizationCode,
                Name = "テスト組織",
                TenantName = TestTenantName
            };
            _context.Organizations.Add(_organization);

            // テスト用の Client をセットアップ
            _client = new Client
            {
                Id = 1,
                ClientId = TestClientId,
                ClientSecret = "test-secret",
                AppName = TestAppName,
                OrganizationId = 1,
                AllowedRpIds = new List<string>()
            };
            _context.Clients.Add(_client);

            _context.SaveChanges();
        }

        #region RequiredMigration Tests

        [Fact]
        public void RequiredMigration_ShouldBeCorrectMigrationName()
        {
            // Assert
            Assert.Equal("20260111034146_AddB2BPasskeyEntities", _seeder.RequiredMigration);
        }

        [Fact]
        public void Order_ShouldBe100()
        {
            // Assert
            Assert.Equal(100, _seeder.Order);
        }

        #endregion

        #region AllowedRpIds Tests

        [Fact]
        public async Task SeedAsync_ShouldAddAllowedRpIds()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_USER_EXTERNAL_ID"] = "test-admin",
                ["DEV_B2B_REDIRECT_URI"] = "https://localhost:8081/admin/callback",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "localhost"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var updatedClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Contains("localhost", updatedClient.AllowedRpIds);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateAllowedRpIds()
        {
            // Arrange
            _client.AllowedRpIds = new List<string> { "localhost" };
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "localhost"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var updatedClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Single(updatedClient.AllowedRpIds, r => r == "localhost");
        }

        [Fact]
        public async Task SeedAsync_ShouldAddMultipleAllowedRpIds_WhenCommaSeparated()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "staging-domain.azurewebsites.net,localhost"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var updatedClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Contains("staging-domain.azurewebsites.net", updatedClient.AllowedRpIds);
            Assert.Contains("localhost", updatedClient.AllowedRpIds);
            Assert.Equal(2, updatedClient.AllowedRpIds.Count);
        }

        [Fact]
        public async Task SeedAsync_ShouldTrimWhitespace_WhenCommaSeparatedAllowedRpIds()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_ALLOWED_RP_IDS"] = " staging-domain.azurewebsites.net , localhost "
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var updatedClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Contains("staging-domain.azurewebsites.net", updatedClient.AllowedRpIds);
            Assert.Contains("localhost", updatedClient.AllowedRpIds);
            Assert.Equal(2, updatedClient.AllowedRpIds.Count);
        }

        [Fact]
        public async Task SeedAsync_ShouldAddOnlyNewRpIds_WhenSomeAlreadyExist()
        {
            // Arrange
            _client.AllowedRpIds = new List<string> { "staging-domain.azurewebsites.net" };
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "staging-domain.azurewebsites.net,localhost"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var updatedClient = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Contains("staging-domain.azurewebsites.net", updatedClient.AllowedRpIds);
            Assert.Contains("localhost", updatedClient.AllowedRpIds);
            Assert.Equal(2, updatedClient.AllowedRpIds.Count);
        }

        [Fact]
        public async Task SeedAsync_CalledMultipleTimes_ShouldBeIdempotent_WithCommaSeparatedRpIds()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "idempotent-subject-2",
                ["DEV_B2B_USER_EXTERNAL_ID"] = "idempotent-admin-2",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "staging-domain.azurewebsites.net,localhost"
            });

            // Act - 3回実行
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);
            Assert.Equal(2, client.AllowedRpIds.Count);
            Assert.Contains("staging-domain.azurewebsites.net", client.AllowedRpIds);
            Assert.Contains("localhost", client.AllowedRpIds);
        }

        #endregion

        #region RedirectUri Tests

        [Fact]
        public async Task SeedAsync_ShouldAddRedirectUri()
        {
            // Arrange
            var redirectUri = "https://localhost:8081/admin/callback";
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_REDIRECT_URI"] = redirectUri
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var exists = await _context.RedirectUris
                .IgnoreQueryFilters()
                .AnyAsync(r => r.Uri == redirectUri && r.ClientId == _client.Id);

            Assert.True(exists);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateRedirectUri()
        {
            // Arrange
            var redirectUri = "https://localhost:8081/admin/callback";
            _context.RedirectUris.Add(new RedirectUri
            {
                Uri = redirectUri,
                ClientId = _client.Id
            });
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid",
                ["DEV_B2B_REDIRECT_URI"] = redirectUri
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var count = await _context.RedirectUris
                .IgnoreQueryFilters()
                .CountAsync(r => r.Uri == redirectUri && r.ClientId == _client.Id);

            Assert.Equal(1, count);
        }

        #endregion

        #region B2BUser Tests

        [Fact]
        public async Task SeedAsync_ShouldCreateB2BUser()
        {
            // Arrange
            var subject = "test-subject-uuid";
            var externalId = "test-admin";
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = subject,
                ["DEV_B2B_USER_EXTERNAL_ID"] = externalId
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var user = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == subject);

            Assert.NotNull(user);
            Assert.Equal(subject, user.Subject);
            Assert.Equal(externalId, user.ExternalId);
            Assert.Equal("admin", user.UserType);
            Assert.Equal(_organization.Id, user.OrganizationId);
        }

        [Fact]
        public async Task SeedAsync_ShouldNotDuplicateB2BUser()
        {
            // Arrange
            var subject = "existing-subject-uuid";
            _context.B2BUsers.Add(new B2BUser
            {
                Subject = subject,
                ExternalId = "existing-admin",
                UserType = "admin",
                OrganizationId = _organization.Id
            });
            await _context.SaveChangesAsync();

            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = subject,
                ["DEV_B2B_USER_EXTERNAL_ID"] = "new-admin"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var count = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync(u => u.Subject == subject);

            Assert.Equal(1, count);

            // ExternalId は更新されていないことを確認
            var user = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstAsync(u => u.Subject == subject);
            Assert.Equal("existing-admin", user.ExternalId);
        }

        #endregion

        #region Environment Prefix Tests

        [Theory]
        [InlineData("Development", "DEV")]
        [InlineData("Staging", "STAGING")]
        [InlineData("Production", "PROD")]
        public async Task SeedAsync_ShouldUseCorrectEnvironmentPrefix(string environment, string expectedPrefix)
        {
            // Arrange
            var subject = $"{expectedPrefix.ToLower()}-subject";
            var configValues = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                [$"{expectedPrefix}_B2B_USER_SUBJECT"] = subject,
                [$"{expectedPrefix}_B2B_USER_EXTERNAL_ID"] = $"{expectedPrefix.ToLower()}-admin"
            };

            // 環境に応じた ClientId/OrganizationCode の設定
            if (environment == "Development")
            {
                configValues["DEFAULT_CLIENT_ID"] = TestClientId;
                configValues["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode;
            }
            else
            {
                configValues[$"{expectedPrefix}_CLIENT_ID"] = TestClientId;
                configValues[$"{expectedPrefix}_ORGANIZATION_CODE"] = TestOrganizationCode;
            }

            var configuration = CreateConfiguration(configValues);

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var user = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == subject);

            Assert.NotNull(user);
        }

        [Fact]
        public async Task SeedAsync_UnknownEnvironment_ShouldDefaultToDev()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Unknown",
                ["DEFAULT_CLIENT_ID"] = TestClientId,
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode,
                ["DEV_B2B_USER_SUBJECT"] = "dev-fallback-subject",
                ["DEV_B2B_USER_EXTERNAL_ID"] = "dev-fallback-admin"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var user = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == "dev-fallback-subject");

            Assert.NotNull(user);
        }

        [Fact]
        public async Task SeedAsync_NullEnvironment_ShouldDefaultToDev()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "dev-default-subject",
                ["DEV_B2B_USER_EXTERNAL_ID"] = "dev-default-admin"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var user = await _context.B2BUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == "dev-default-subject");

            Assert.NotNull(user);
        }

        #endregion

        #region Skip Conditions Tests

        [Fact]
        public async Task SeedAsync_WithoutB2BUserSubject_ShouldSkip()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>());

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, userCount);
        }

        [Fact]
        public async Task SeedAsync_WithoutB2BUserExternalId_ShouldSkip()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "test-subject-uuid"
                // DEV_B2B_USER_EXTERNAL_ID is not set
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, userCount);
        }

        [Fact]
        public async Task SeedAsync_WithoutClientId_ShouldSkip()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                // DEFAULT_CLIENT_ID is not set
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode,
                ["DEV_B2B_USER_SUBJECT"] = "test-subject"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, userCount);
        }

        [Fact]
        public async Task SeedAsync_WithNonExistentClient_ShouldSkip()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_CLIENT_ID"] = "non-existent-client",
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode,
                ["DEV_B2B_USER_SUBJECT"] = "test-subject"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, userCount);
        }

        [Fact]
        public async Task SeedAsync_WithNonExistentOrganization_ShouldSkipB2BUserCreation()
        {
            // Arrange
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_CLIENT_ID"] = TestClientId,
                ["DEFAULT_ORGANIZATION_CODE"] = "non-existent-org",
                ["DEV_B2B_USER_SUBJECT"] = "test-subject",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "localhost"
            });

            // Act
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            // AllowedRpIds は追加されるが、B2BUser は作成されない
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync();

            Assert.Equal(0, userCount);

            // AllowedRpIds は追加される
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);

            Assert.Contains("localhost", client.AllowedRpIds);
        }

        #endregion

        #region Idempotency Tests

        [Fact]
        public async Task SeedAsync_CalledMultipleTimes_ShouldBeIdempotent()
        {
            // Arrange
            var configuration = CreateDevConfiguration(new Dictionary<string, string?>
            {
                ["DEV_B2B_USER_SUBJECT"] = "idempotent-subject",
                ["DEV_B2B_USER_EXTERNAL_ID"] = "idempotent-admin",
                ["DEV_B2B_REDIRECT_URI"] = "https://localhost:8081/admin/callback",
                ["DEV_B2B_ALLOWED_RP_IDS"] = "localhost"
            });

            // Act - 3回実行
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);
            await _seeder.SeedAsync(_context, configuration, _mockLogger.Object);

            // Assert
            var userCount = await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync(u => u.Subject == "idempotent-subject");
            Assert.Equal(1, userCount);

            var redirectUriCount = await _context.RedirectUris
                .IgnoreQueryFilters()
                .CountAsync(r => r.Uri == "https://localhost:8081/admin/callback");
            Assert.Equal(1, redirectUriCount);

            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstAsync(c => c.ClientId == TestClientId);
            Assert.Single(client.AllowedRpIds, r => r == "localhost");
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Development 環境用の共通設定を含む Configuration を作成
        /// </summary>
        private static IConfiguration CreateDevConfiguration(Dictionary<string, string?> additionalValues)
        {
            var baseValues = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DEFAULT_CLIENT_ID"] = TestClientId,
                ["DEFAULT_ORGANIZATION_CODE"] = TestOrganizationCode
            };

            foreach (var kvp in additionalValues)
            {
                baseValues[kvp.Key] = kvp.Value;
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(baseValues)
                .Build();
        }

        /// <summary>
        /// カスタム Configuration を作成（環境固有のテスト用）
        /// </summary>
        private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        #endregion
    }
}
