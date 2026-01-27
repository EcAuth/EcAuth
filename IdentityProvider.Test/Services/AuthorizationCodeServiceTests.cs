using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    public class AuthorizationCodeServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly AuthorizationCodeService _service;
        private readonly Mock<ILogger<AuthorizationCodeService>> _mockLogger;

        public AuthorizationCodeServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockLogger = new Mock<ILogger<AuthorizationCodeService>>();
            _service = new AuthorizationCodeService(_context, _mockLogger.Object);

            // テスト用のテナントとクライアントをセットアップ
            SetupTestData();
        }

        private void SetupTestData()
        {
            var organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            };

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1,
                Organization = organization
            };

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-hash",
                OrganizationId = 1,
                Organization = organization
            };

            _context.Organizations.Add(organization);
            _context.Clients.Add(client);
            _context.EcAuthUsers.Add(user);
            _context.SaveChanges();
        }

        [Fact]
        public async Task GenerateAuthorizationCodeAsync_ValidRequest_ReturnsUniqueCode()
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpirationMinutes = 10
            };

            // Act
            var result = await _service.GenerateAuthorizationCodeAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.Code);
            Assert.True(result.Code.Length >= 32); // Base64URLエンコード後の最小長
            Assert.Equal(request.Subject, result.Subject);
            Assert.Equal(request.ClientId, result.ClientId);
            Assert.Equal(request.RedirectUri, result.RedirectUri);
            Assert.Equal(request.Scope, result.Scope);
            Assert.False(result.IsUsed);
            Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
        }

        [Fact]
        public async Task GenerateAuthorizationCodeAsync_GeneratesUniqueCodes()
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile"
            };

            // Act & Assert
            var codes = new HashSet<string>();
            for (int i = 0; i < 3; i++)  // レート制限を考慮して3つに減少
            {
                var uniqueRequest = new IAuthorizationCodeService.AuthorizationCodeRequest
                {
                    Subject = $"test-subject-{i}",  // 異なるSubjectを使用
                    ClientId = 1,
                    RedirectUri = "https://example.com/callback",
                    Scope = "openid profile"
                };

                var result = await _service.GenerateAuthorizationCodeAsync(uniqueRequest);
                Assert.True(codes.Add(result.Code), "重複コードが生成されました");
            }
        }

        [Theory]
        [InlineData("", 1, "https://example.com/callback")] // 空のSubject
        [InlineData("test-subject", 0, "https://example.com/callback")] // 無効なClientId
        [InlineData("test-subject", 1, "")] // 空のRedirectUri
        [InlineData("test-subject", 1, "invalid-url")] // 無効なURL
        public async Task GenerateAuthorizationCodeAsync_InvalidRequest_ThrowsArgumentException(
            string subject, int clientId, string redirectUri)
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = subject,
                ClientId = clientId,
                RedirectUri = redirectUri
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateAuthorizationCodeAsync(request));
        }

        [Fact]
        public async Task GenerateAuthorizationCodeAsync_ExpirationMinutesOutOfRange_ThrowsArgumentException()
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpirationMinutes = 35 // 30分を超える
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateAuthorizationCodeAsync(request));
        }

        // レート制限機能は削除されたため、このテストも削除
        // [Fact]
        // public async Task GenerateAuthorizationCodeAsync_RateLimitExceeded_ThrowsInvalidOperationException()

        [Fact]
        public async Task GetAuthorizationCodeAsync_ValidCode_ReturnsCode()
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback"
            };
            var generated = await _service.GenerateAuthorizationCodeAsync(request);

            // Act
            var result = await _service.GetAuthorizationCodeAsync(generated.Code);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(generated.Code, result.Code);
            Assert.False(result.IsUsed);
        }

        [Fact]
        public async Task GetAuthorizationCodeAsync_ExpiredCode_ReturnsNull()
        {
            // Arrange
            var authorizationCode = new AuthorizationCode
            {
                Code = "expired-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), // 既に期限切れ
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            _context.AuthorizationCodes.Add(authorizationCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetAuthorizationCodeAsync("expired-code");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetAuthorizationCodeAsync_UsedCode_ReturnsNull()
        {
            // Arrange
            var authorizationCode = new AuthorizationCode
            {
                Code = "used-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = true, // 既に使用済み
                UsedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            };
            _context.AuthorizationCodes.Add(authorizationCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetAuthorizationCodeAsync("used-code");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetAuthorizationCodeAsync_NonExistentCode_ReturnsNull()
        {
            // Act
            var result = await _service.GetAuthorizationCodeAsync("non-existent-code");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task MarkAsUsedAsync_ValidCode_MarksAsUsed()
        {
            // Arrange
            var request = new IAuthorizationCodeService.AuthorizationCodeRequest
            {
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback"
            };
            var generated = await _service.GenerateAuthorizationCodeAsync(request);

            // Act
            var result = await _service.MarkAsUsedAsync(generated.Code);

            // Assert
            Assert.True(result);

            var updated = await _context.AuthorizationCodes
                .FirstOrDefaultAsync(ac => ac.Code == generated.Code);
            Assert.NotNull(updated);
            Assert.True(updated.IsUsed);
            Assert.NotNull(updated.UsedAt);
        }

        [Fact]
        public async Task MarkAsUsedAsync_AlreadyUsedCode_ReturnsFalse()
        {
            // Arrange
            var authorizationCode = new AuthorizationCode
            {
                Code = "already-used-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = true,
                UsedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            };
            _context.AuthorizationCodes.Add(authorizationCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.MarkAsUsedAsync("already-used-code");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task MarkAsUsedAsync_NonExistentCode_ReturnsFalse()
        {
            // Act
            var result = await _service.MarkAsUsedAsync("non-existent-code");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task CleanupExpiredCodesAsync_RemovesExpiredCodes()
        {
            // Arrange
            var expiredCode1 = new AuthorizationCode
            {
                Code = "expired-code-1",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15)
            };

            var expiredCode2 = new AuthorizationCode
            {
                Code = "expired-code-2",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };

            var validCode = new AuthorizationCode
            {
                Code = "valid-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _context.AuthorizationCodes.AddRange(expiredCode1, expiredCode2, validCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CleanupExpiredCodesAsync();

            // Assert
            Assert.Equal(2, result);

            var remaining = await _context.AuthorizationCodes.ToListAsync();
            Assert.Single(remaining);
            Assert.Equal("valid-code", remaining[0].Code);
        }

        [Fact]
        public async Task GetStatisticsAsync_ReturnsCorrectStatistics()
        {
            // Arrange
            var activeCode = new AuthorizationCode
            {
                Code = "active-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var usedCode = new AuthorizationCode
            {
                Code = "used-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = true,
                UsedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            };

            var expiredCode = new AuthorizationCode
            {
                Code = "expired-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };

            _context.AuthorizationCodes.AddRange(activeCode, usedCode, expiredCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetStatisticsAsync();

            // Assert
            Assert.Equal(3, result.TotalCodes);
            Assert.Equal(1, result.ActiveCodes);
            Assert.Equal(1, result.ExpiredCodes);
            Assert.Equal(1, result.UsedCodes);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}