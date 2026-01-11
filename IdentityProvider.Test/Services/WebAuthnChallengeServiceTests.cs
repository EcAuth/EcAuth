using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    public class WebAuthnChallengeServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly WebAuthnChallengeService _service;
        private readonly Mock<ILogger<WebAuthnChallengeService>> _mockLogger;
        private readonly Organization _organization;
        private readonly Client _client;

        public WebAuthnChallengeServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockLogger = new Mock<ILogger<WebAuthnChallengeService>>();
            _service = new WebAuthnChallengeService(_context, _mockLogger.Object);

            // テスト用のテナントとクライアントをセットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            };

            _client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1,
                Organization = _organization
            };

            _context.Organizations.Add(_organization);
            _context.Clients.Add(_client);
            _context.SaveChanges();
        }

        #region GenerateChallengeAsync Tests

        [Fact]
        public async Task GenerateChallengeAsync_ValidB2BRequest_ShouldCreateChallenge()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act
            var result = await _service.GenerateChallengeAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.SessionId);
            Assert.NotEmpty(result.Challenge);
            Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.True(result.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(6)); // 5分 + バッファ

            // DBに保存されていることを確認
            var saved = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.SessionId == result.SessionId);
            Assert.NotNull(saved);
            Assert.Equal(request.Type, saved.Type);
            Assert.Equal(request.UserType, saved.UserType);
            Assert.Equal(request.Subject, saved.Subject);
            Assert.Equal(request.RpId, saved.RpId);
            Assert.Equal(request.ClientId, saved.ClientId);
        }

        [Fact]
        public async Task GenerateChallengeAsync_ValidB2CAuthenticationRequest_ShouldCreateChallenge()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "authentication",
                UserType = "b2c",
                Subject = "test-b2c-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act
            var result = await _service.GenerateChallengeAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.SessionId);
        }

        [Fact]
        public async Task GenerateChallengeAsync_B2BWithoutSubject_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = null, // B2Bでは必須
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateChallengeAsync(request));
            Assert.Contains("Subject", ex.Message);
        }

        [Fact]
        public async Task GenerateChallengeAsync_B2CAuthenticationWithoutSubject_ShouldThrowArgumentException()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "authentication",
                UserType = "b2c",
                Subject = null, // B2C認証では必須
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateChallengeAsync(request));
            Assert.Contains("Subject", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid")]
        [InlineData("register")] // typo
        public async Task GenerateChallengeAsync_InvalidType_ShouldThrowArgumentException(string type)
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = type,
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateChallengeAsync(request));
            Assert.Contains("Type", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid")]
        [InlineData("B2B")] // 大文字
        public async Task GenerateChallengeAsync_InvalidUserType_ShouldThrowArgumentException(string userType)
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = userType,
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateChallengeAsync(request));
            Assert.Contains("UserType", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task GenerateChallengeAsync_InvalidClientId_ShouldThrowArgumentException(int clientId)
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = clientId
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.GenerateChallengeAsync(request));
            Assert.Contains("ClientId", ex.Message);
        }

        [Fact]
        public async Task GenerateChallengeAsync_ShouldGenerateUniqueSessionId()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act
            var sessionIds = new HashSet<string>();
            for (int i = 0; i < 5; i++)
            {
                var result = await _service.GenerateChallengeAsync(request);
                Assert.True(sessionIds.Add(result.SessionId), "重複したSessionIdが生成されました");
            }

            // Assert
            Assert.Equal(5, sessionIds.Count);
        }

        [Fact]
        public async Task GenerateChallengeAsync_ChallengeShouldBe32BytesBase64Url()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };

            // Act
            var result = await _service.GenerateChallengeAsync(request);

            // Assert
            var decoded = Base64UrlTextEncoder.Decode(result.Challenge);
            Assert.True(decoded.Length >= 32, $"チャレンジは32バイト以上である必要があります。実際: {decoded.Length}バイト");
        }

        [Fact]
        public async Task GenerateChallengeAsync_ExpiresAtShouldBe5MinutesFromNow()
        {
            // Arrange
            var request = new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            };
            var before = DateTimeOffset.UtcNow;

            // Act
            var result = await _service.GenerateChallengeAsync(request);

            // Assert
            var after = DateTimeOffset.UtcNow;
            var expectedMinExpiresAt = before.AddMinutes(5);
            var expectedMaxExpiresAt = after.AddMinutes(5);

            Assert.True(result.ExpiresAt >= expectedMinExpiresAt.AddSeconds(-1));
            Assert.True(result.ExpiresAt <= expectedMaxExpiresAt.AddSeconds(1));
        }

        [Fact]
        public async Task GenerateChallengeAsync_ConcurrentRequests_ShouldGenerateUniqueSessionIds()
        {
            // Arrange
            var requests = Enumerable.Range(0, 10).Select(_ => new IWebAuthnChallengeService.ChallengeRequest
            {
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1
            }).ToList();

            // Act - 10件の並行リクエスト
            var tasks = requests.Select(r => _service.GenerateChallengeAsync(r));
            var results = await Task.WhenAll(tasks);

            // Assert - 全てのSessionIdがユニークであること
            var uniqueSessionIds = results.Select(r => r.SessionId).Distinct().Count();
            Assert.Equal(10, uniqueSessionIds);

            // 全てのチャレンジもユニークであること
            var uniqueChallenges = results.Select(r => r.Challenge).Distinct().Count();
            Assert.Equal(10, uniqueChallenges);
        }

        #endregion

        #region GetChallengeBySessionIdAsync Tests

        [Fact]
        public async Task GetChallengeBySessionIdAsync_ExistingChallenge_ShouldReturn()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Challenge = "test-challenge",
                SessionId = "test-session-id",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow,
                Client = _client
            };
            _context.WebAuthnChallenges.Add(challenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetChallengeBySessionIdAsync("test-session-id");

            // Assert
            Assert.NotNull(result);
            Assert.Equal("test-session-id", result.SessionId);
            Assert.Equal("test-challenge", result.Challenge);
        }

        [Fact]
        public async Task GetChallengeBySessionIdAsync_ExpiredChallenge_ShouldReturnNull()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Challenge = "test-challenge",
                SessionId = "expired-session-id",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), // 期限切れ
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
                Client = _client
            };
            _context.WebAuthnChallenges.Add(challenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetChallengeBySessionIdAsync("expired-session-id");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetChallengeBySessionIdAsync_NonExisting_ShouldReturnNull()
        {
            // Act
            var result = await _service.GetChallengeBySessionIdAsync("non-existing-session-id");

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetChallengeBySessionIdAsync_InvalidSessionId_ShouldReturnNull(string? sessionId)
        {
            // Act
            var result = await _service.GetChallengeBySessionIdAsync(sessionId!);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region ConsumeChallengeAsync Tests

        [Fact]
        public async Task ConsumeChallengeAsync_ExistingChallenge_ShouldDeleteAndReturnTrue()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Challenge = "test-challenge",
                SessionId = "consume-test-session-id",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow,
                Client = _client
            };
            _context.WebAuthnChallenges.Add(challenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.ConsumeChallengeAsync("consume-test-session-id");

            // Assert
            Assert.True(result);

            var deleted = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.SessionId == "consume-test-session-id");
            Assert.Null(deleted);
        }

        [Fact]
        public async Task ConsumeChallengeAsync_NonExisting_ShouldReturnFalse()
        {
            // Act
            var result = await _service.ConsumeChallengeAsync("non-existing-session-id");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task ConsumeChallengeAsync_ExpiredChallenge_ShouldDeleteAndReturnTrue()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Challenge = "test-challenge",
                SessionId = "expired-consume-session-id",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), // 期限切れ
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
                Client = _client
            };
            _context.WebAuthnChallenges.Add(challenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.ConsumeChallengeAsync("expired-consume-session-id");

            // Assert
            Assert.True(result);

            var deleted = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.SessionId == "expired-consume-session-id");
            Assert.Null(deleted);
        }

        #endregion

        #region CleanupExpiredChallengesAsync Tests

        [Fact]
        public async Task CleanupExpiredChallengesAsync_WithExpiredChallenges_ShouldDeleteThem()
        {
            // Arrange
            var expiredChallenge1 = new WebAuthnChallenge
            {
                Challenge = "expired-challenge-1",
                SessionId = "expired-session-1",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
                Client = _client
            };

            var expiredChallenge2 = new WebAuthnChallenge
            {
                Challenge = "expired-challenge-2",
                SessionId = "expired-session-2",
                Type = "authentication",
                UserType = "b2b",
                Subject = "test-subject",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                Client = _client
            };

            _context.WebAuthnChallenges.AddRange(expiredChallenge1, expiredChallenge2);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CleanupExpiredChallengesAsync();

            // Assert
            Assert.Equal(2, result);

            var remaining = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .ToListAsync();
            Assert.Empty(remaining);
        }

        [Fact]
        public async Task CleanupExpiredChallengesAsync_NoExpiredChallenges_ShouldReturnZero()
        {
            // Arrange
            var validChallenge = new WebAuthnChallenge
            {
                Challenge = "valid-challenge",
                SessionId = "valid-session",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow,
                Client = _client
            };
            _context.WebAuthnChallenges.Add(validChallenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CleanupExpiredChallengesAsync();

            // Assert
            Assert.Equal(0, result);

            var remaining = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .ToListAsync();
            Assert.Single(remaining);
        }

        [Fact]
        public async Task CleanupExpiredChallengesAsync_MixedChallenges_ShouldOnlyDeleteExpired()
        {
            // Arrange
            var expiredChallenge = new WebAuthnChallenge
            {
                Challenge = "expired-challenge",
                SessionId = "expired-session",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-subject",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                Client = _client
            };

            var validChallenge = new WebAuthnChallenge
            {
                Challenge = "valid-challenge",
                SessionId = "valid-session",
                Type = "authentication",
                UserType = "b2b",
                Subject = "test-subject",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow,
                Client = _client
            };

            _context.WebAuthnChallenges.AddRange(expiredChallenge, validChallenge);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CleanupExpiredChallengesAsync();

            // Assert
            Assert.Equal(1, result);

            var remaining = await _context.WebAuthnChallenges
                .IgnoreQueryFilters()
                .ToListAsync();
            Assert.Single(remaining);
            Assert.Equal("valid-session", remaining[0].SessionId);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
