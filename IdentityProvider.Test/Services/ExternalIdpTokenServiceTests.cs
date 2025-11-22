using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Test.Services
{
    public class ExternalIdpTokenServiceTests
    {
        private readonly ILogger<ExternalIdpTokenService> _logger;

        public ExternalIdpTokenServiceTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<ExternalIdpTokenService>();
        }

        [Fact]
        public async Task SaveTokenAsync_NewToken_ShouldSaveSuccessfully()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var request = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act
            var result = await service.SaveTokenAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(user.Subject, result.EcAuthSubject);
            Assert.Equal("google-oauth2", result.ExternalProvider);
            Assert.Equal("test-access-token", result.AccessToken);
            Assert.Equal("test-refresh-token", result.RefreshToken);
            Assert.False(result.IsExpired);

            // Verify token is saved in database
            var savedToken = await context.ExternalIdpTokens.FirstOrDefaultAsync(
                t => t.EcAuthSubject == user.Subject && t.ExternalProvider == "google-oauth2");
            Assert.NotNull(savedToken);
        }

        [Fact]
        public async Task SaveTokenAsync_ExistingToken_ShouldUpdateSuccessfully()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);

            // First save
            var firstRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "old-access-token",
                RefreshToken = "old-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(firstRequest);

            // Second save (update)
            var updateRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "new-access-token",
                RefreshToken = "new-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            };

            // Act
            var result = await service.SaveTokenAsync(updateRequest);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("new-access-token", result.AccessToken);
            Assert.Equal("new-refresh-token", result.RefreshToken);

            // Verify only one token exists in database
            var tokens = await context.ExternalIdpTokens
                .Where(t => t.EcAuthSubject == user.Subject && t.ExternalProvider == "google-oauth2")
                .ToListAsync();
            Assert.Single(tokens);
        }

        [Fact]
        public async Task SaveTokenAsync_NullEcAuthSubject_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var request = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = null!,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveTokenAsync(request));
        }

        [Fact]
        public async Task SaveTokenAsync_EmptyExternalProvider_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var request = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "",
                AccessToken = "test-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveTokenAsync(request));
        }

        [Fact]
        public async Task SaveTokenAsync_NullAccessToken_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var request = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = null!,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveTokenAsync(request));
        }

        [Fact]
        public async Task GetTokenAsync_ValidToken_ShouldReturnToken()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var saveRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(saveRequest);

            // Act
            var result = await service.GetTokenAsync(user.Subject, "google-oauth2");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(user.Subject, result.EcAuthSubject);
            Assert.Equal("google-oauth2", result.ExternalProvider);
            Assert.Equal("test-access-token", result.AccessToken);
            Assert.Equal("test-refresh-token", result.RefreshToken);
            Assert.False(result.IsExpired);
        }

        [Fact]
        public async Task GetTokenAsync_TokenNotFound_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);

            // Act
            var result = await service.GetTokenAsync(user.Subject, "google-oauth2");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetTokenAsync_ExpiredToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var saveRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) // Expired 1 hour ago
            };
            await service.SaveTokenAsync(saveRequest);

            // Act
            var result = await service.GetTokenAsync(user.Subject, "google-oauth2");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetTokenAsync_NullEcAuthSubject_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GetTokenAsync(null!, "google-oauth2"));
        }

        [Fact]
        public async Task GetTokenAsync_EmptyExternalProvider_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GetTokenAsync(user.Subject, ""));
        }

        [Fact]
        public async Task RefreshTokenAsync_NotImplemented_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var saveRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(saveRequest);

            // Act
            var result = await service.RefreshTokenAsync(user.Subject, "google-oauth2");

            // Assert
            // RefreshTokenAsync is not yet implemented (Phase 2 feature)
            Assert.Null(result);
        }

        [Fact]
        public async Task RefreshTokenAsync_TokenNotFound_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);

            // Act
            var result = await service.RefreshTokenAsync(user.Subject, "google-oauth2");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task RefreshTokenAsync_NoRefreshToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var saveRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "test-access-token",
                RefreshToken = null, // No refresh token
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(saveRequest);

            // Act
            var result = await service.RefreshTokenAsync(user.Subject, "google-oauth2");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task CleanupExpiredTokensAsync_WithExpiredTokens_ShouldDeleteThem()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user1 = await SetupTestUserAsync(context, "user-1");
            var user2 = await SetupTestUserAsync(context, "user-2");

            // Save expired token
            var expiredRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user1.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "expired-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
            };
            await service.SaveTokenAsync(expiredRequest);

            // Save valid token
            var validRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user2.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "valid-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(validRequest);

            // Act
            var deletedCount = await service.CleanupExpiredTokensAsync();

            // Assert
            Assert.Equal(1, deletedCount);

            // Verify expired token is removed
            var expiredToken = await context.ExternalIdpTokens.FirstOrDefaultAsync(
                t => t.EcAuthSubject == user1.Subject);
            Assert.Null(expiredToken);

            // Verify valid token still exists
            var validToken = await context.ExternalIdpTokens.FirstOrDefaultAsync(
                t => t.EcAuthSubject == user2.Subject);
            Assert.NotNull(validToken);
        }

        [Fact]
        public async Task CleanupExpiredTokensAsync_NoExpiredTokens_ShouldReturnZero()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var validRequest = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = "google-oauth2",
                AccessToken = "valid-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            await service.SaveTokenAsync(validRequest);

            // Act
            var deletedCount = await service.CleanupExpiredTokensAsync();

            // Assert
            Assert.Equal(0, deletedCount);

            // Verify token still exists
            var token = await context.ExternalIdpTokens.FirstOrDefaultAsync(
                t => t.EcAuthSubject == user.Subject);
            Assert.NotNull(token);
        }

        [Theory]
        [InlineData("google-oauth2")]
        [InlineData("federate-oauth2")]
        [InlineData("line-oauth2")]
        public async Task SaveTokenAsync_DifferentProviders_ShouldSaveSeparately(string provider)
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new ExternalIdpTokenService(context, _logger);

            // Arrange
            var user = await SetupTestUserAsync(context);
            var request = new IExternalIdpTokenService.SaveTokenRequest
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = provider,
                AccessToken = $"{provider}-access-token",
                RefreshToken = $"{provider}-refresh-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act
            var result = await service.SaveTokenAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(provider, result.ExternalProvider);
            Assert.Equal($"{provider}-access-token", result.AccessToken);
        }

        private async Task<EcAuthUser> SetupTestUserAsync(EcAuthDbContext context, string? subjectSuffix = null)
        {
            var organization = new Organization
            {
                Code = "TESTORG",
                Name = "TestOrg",
                TenantName = "test-tenant"
            };
            context.Organizations.Add(organization);

            var subject = subjectSuffix != null ? $"test-subject-{subjectSuffix}" : "test-subject";
            var user = new EcAuthUser
            {
                Subject = subject,
                EmailHash = EmailHashUtil.HashEmail($"{subject}@example.com"),
                OrganizationId = organization.Id
            };
            context.EcAuthUsers.Add(user);

            await context.SaveChangesAsync();

            return user;
        }
    }
}
