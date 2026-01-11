using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

namespace IdentityProvider.Test.Services
{
    public class B2BPasskeyServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly Mock<IFido2> _mockFido2;
        private readonly Mock<IWebAuthnChallengeService> _mockChallengeService;
        private readonly Mock<IB2BUserService> _mockUserService;
        private readonly Mock<ILogger<B2BPasskeyService>> _mockLogger;
        private readonly B2BPasskeyService _service;
        private readonly Organization _organization;
        private readonly Client _client;
        private readonly B2BUser _testUser;

        public B2BPasskeyServiceTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockFido2 = new Mock<IFido2>();
            _mockChallengeService = new Mock<IWebAuthnChallengeService>();
            _mockUserService = new Mock<IB2BUserService>();
            _mockLogger = new Mock<ILogger<B2BPasskeyService>>();

            _service = new B2BPasskeyService(
                _context,
                _mockFido2.Object,
                _mockChallengeService.Object,
                _mockUserService.Object,
                _mockLogger.Object);

            // テスト用のテナント・クライアントをセットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant"
            };
            _context.Organizations.Add(_organization);

            _client = new Client
            {
                Id = 1,
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AppName = "テストクライアント",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com", "admin.example.com" }
            };
            _context.Clients.Add(_client);

            _testUser = new B2BUser
            {
                Id = 1,
                Subject = "test-b2b-subject",
                ExternalId = "admin@example.com",
                UserType = "admin",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(_testUser);

            _context.SaveChanges();
        }

        #region CreateRegistrationOptionsAsync Tests

        [Fact]
        public async Task CreateRegistrationOptionsAsync_ValidRequest_ShouldReturnOptions()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = "test-b2b-subject",
                DisplayName = "テスト管理者",
                DeviceName = "MacBook Pro"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync("test-b2b-subject"))
                .ReturnsAsync(_testUser);

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "session-123",
                Challenge = "challenge-base64url",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var credentialCreateOptions = new CredentialCreateOptions
            {
                Challenge = Encoding.UTF8.GetBytes("test-challenge"),
                Rp = new PublicKeyCredentialRpEntity("shop.example.com", "テスト組織"),
                User = new Fido2User
                {
                    Id = Encoding.UTF8.GetBytes("test-b2b-subject"),
                    Name = "admin@example.com",
                    DisplayName = "テスト管理者"
                },
                PubKeyCredParams = PubKeyCredParam.Defaults
            };
            _mockFido2.Setup(x => x.RequestNewCredential(It.IsAny<RequestNewCredentialParams>()))
                .Returns(credentialCreateOptions);

            // Act
            var result = await _service.CreateRegistrationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyClientId_ShouldThrowArgumentException(string clientId)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = clientId,
                RpId = "shop.example.com",
                B2BSubject = "test-b2b-subject"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("ClientId", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyRpId_ShouldThrowArgumentException(string rpId)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = rpId,
                B2BSubject = "test-b2b-subject"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("RpId", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRegistrationOptionsAsync_EmptyB2BSubject_ShouldThrowArgumentException(string subject)
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = subject
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("B2BSubject", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_NonExistingClient_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "non-existing-client",
                RpId = "shop.example.com",
                B2BSubject = "test-b2b-subject"
            };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("Client", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_NonExistingUser_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = "non-existing-subject"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync("non-existing-subject"))
                .ReturnsAsync((B2BUser?)null);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("B2BUser", ex.Message);
        }

        [Fact]
        public async Task CreateRegistrationOptionsAsync_RpIdNotAllowed_ShouldThrowInvalidOperationException()
        {
            // Arrange
            var request = new IB2BPasskeyService.RegistrationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "unauthorized.example.com", // AllowedRpIdsに含まれない
                B2BSubject = "test-b2b-subject"
            };

            _mockUserService.Setup(x => x.GetBySubjectAsync("test-b2b-subject"))
                .ReturnsAsync(_testUser);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.CreateRegistrationOptionsAsync(request));
            Assert.Contains("RpId", ex.Message);
        }

        #endregion

        #region VerifyRegistrationAsync Tests

        [Fact]
        public async Task VerifyRegistrationAsync_ValidRequest_ShouldSaveCredential()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                Id = 1,
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U", // Base64URL
                Type = "registration",
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-123"))
                .ReturnsAsync(challenge);

            var credentialIdBytes = Encoding.UTF8.GetBytes("credential-id");
            var attestationResponse = new AuthenticatorAttestationRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                {
                    AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data")
                }
            };

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-123",
                ClientId = "test-client-id",
                AttestationResponse = attestationResponse,
                DeviceName = "MacBook Pro"
            };

            var makeCredentialResult = new RegisteredPublicKeyCredential
            {
                Id = Encoding.UTF8.GetBytes("credential-id"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid(),
                AttestationObject = Encoding.UTF8.GetBytes("attestation"),
                AttestationClientDataJson = Encoding.UTF8.GetBytes("client-data")
            };

            _mockFido2.Setup(x => x.MakeNewCredentialAsync(
                It.IsAny<MakeNewCredentialParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(makeCredentialResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("session-123"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.CredentialId);

            // DBに保存されていることを確認
            var saved = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == "test-b2b-subject");
            Assert.NotNull(saved);
            Assert.Equal("MacBook Pro", saved.DeviceName);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_NonExistingSession_ShouldReturnFailure()
        {
            // Arrange
            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("non-existing"))
                .ReturnsAsync((WebAuthnChallenge?)null);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "non-existing",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Session", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_ExpiredChallenge_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "expired-session",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "registration",
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) // 期限切れ
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("expired-session"))
                .ReturnsAsync(challenge);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "expired-session",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task VerifyRegistrationAsync_WrongChallengeType_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "session-123",
                Challenge = "dGVzdC1jaGFsbGVuZ2U",
                Type = "authentication", // 登録ではなく認証
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("session-123"))
                .ReturnsAsync(challenge);

            var request = new IB2BPasskeyService.RegistrationVerifyRequest
            {
                SessionId = "session-123",
                ClientId = "test-client-id",
                AttestationResponse = new AuthenticatorAttestationRawResponse()
            };

            // Act
            var result = await _service.VerifyRegistrationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("type", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region CreateAuthenticationOptionsAsync Tests

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_ValidRequest_ShouldReturnOptions()
        {
            // Arrange
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = "test-b2b-subject",
                CredentialId = Encoding.UTF8.GetBytes("credential-id"),
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 0,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                Transports = new[] { "internal" }
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = "test-b2b-subject"
            };

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-123",
                Challenge = "auth-challenge-base64url",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var assertionOptions = new AssertionOptions
            {
                Challenge = Encoding.UTF8.GetBytes("auth-challenge"),
                RpId = "shop.example.com"
            };
            _mockFido2.Setup(x => x.GetAssertionOptions(It.IsAny<GetAssertionOptionsParams>()))
                .Returns(assertionOptions);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("auth-session-123", result.SessionId);
            Assert.NotNull(result.Options);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_WithoutSubject_ShouldGetAllCredentialsForRpId()
        {
            // Arrange
            // 複数ユーザーのクレデンシャルを追加
            var user2 = new B2BUser
            {
                Subject = "test-b2b-subject-2",
                ExternalId = "staff@example.com",
                UserType = "staff",
                OrganizationId = 1,
                Organization = _organization
            };
            _context.B2BUsers.Add(user2);

            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("credential-1"),
                    PublicKey = Encoding.UTF8.GetBytes("public-key-1"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject-2",
                    CredentialId = Encoding.UTF8.GetBytes("credential-2"),
                    PublicKey = Encoding.UTF8.GetBytes("public-key-2"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = null // 全ユーザー
            };

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-456",
                Challenge = "auth-challenge",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var assertionOptions = new AssertionOptions
            {
                Challenge = Encoding.UTF8.GetBytes("auth-challenge"),
                RpId = "shop.example.com"
            };
            _mockFido2.Setup(x => x.GetAssertionOptions(It.IsAny<GetAssertionOptionsParams>()))
                .Returns(assertionOptions);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            // GetAssertionOptionsに渡されたクレデンシャル数を確認
            _mockFido2.Verify(x => x.GetAssertionOptions(
                It.Is<GetAssertionOptionsParams>(p => p.AllowedCredentials.Count == 2)), Times.Once);
        }

        [Fact]
        public async Task CreateAuthenticationOptionsAsync_NoCredentials_ShouldReturnEmptyAllowCredentials()
        {
            // Arrange
            var request = new IB2BPasskeyService.AuthenticationOptionsRequest
            {
                ClientId = "test-client-id",
                RpId = "shop.example.com",
                B2BSubject = "user-with-no-passkeys"
            };

            var challengeResult = new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = "auth-session-789",
                Challenge = "auth-challenge",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mockChallengeService.Setup(x => x.GenerateChallengeAsync(It.IsAny<IWebAuthnChallengeService.ChallengeRequest>()))
                .ReturnsAsync(challengeResult);

            var assertionOptions = new AssertionOptions
            {
                Challenge = Encoding.UTF8.GetBytes("auth-challenge"),
                RpId = "shop.example.com"
            };
            _mockFido2.Setup(x => x.GetAssertionOptions(It.IsAny<GetAssertionOptionsParams>()))
                .Returns(assertionOptions);

            // Act
            var result = await _service.CreateAuthenticationOptionsAsync(request);

            // Assert
            Assert.NotNull(result);
            _mockFido2.Verify(x => x.GetAssertionOptions(
                It.Is<GetAssertionOptionsParams>(p => p.AllowedCredentials.Count == 0)), Times.Once);
        }

        #endregion

        #region VerifyAuthenticationAsync Tests

        [Fact]
        public async Task VerifyAuthenticationAsync_ValidRequest_ShouldReturnSuccessAndUpdateSignCount()
        {
            // Arrange
            var credentialIdBytes = Encoding.UTF8.GetBytes("auth-credential-id");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = "test-b2b-subject",
                CredentialId = credentialIdBytes,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 5,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var challenge = new WebAuthnChallenge
            {
                SessionId = "auth-session",
                Challenge = "YXV0aC1jaGFsbGVuZ2U", // Base64URL of "auth-challenge"
                Type = "authentication",
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("auth-session"))
                .ReturnsAsync(challenge);

            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(credentialIdBytes),
                RawId = credentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "auth-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            var verifyResult = new VerifyAssertionResult
            {
                SignCount = 6
            };

            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyResult);

            _mockChallengeService.Setup(x => x.ConsumeChallengeAsync("auth-session"))
                .ReturnsAsync(true);

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal("test-b2b-subject", result.B2BSubject);
            Assert.NotNull(result.CredentialId);

            // SignCountが更新されていることを確認
            var updated = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == "test-b2b-subject");
            Assert.NotNull(updated);
            Assert.Equal(6u, updated.SignCount);
            Assert.NotNull(updated.LastUsedAt);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_NonExistingSession_ShouldReturnFailure()
        {
            // Arrange
            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("non-existing"))
                .ReturnsAsync((WebAuthnChallenge?)null);

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "non-existing",
                ClientId = "test-client-id",
                AssertionResponse = new AuthenticatorAssertionRawResponse()
            };

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Session", result.ErrorMessage);
        }

        [Fact]
        public async Task VerifyAuthenticationAsync_CredentialNotFound_ShouldReturnFailure()
        {
            // Arrange
            var challenge = new WebAuthnChallenge
            {
                SessionId = "auth-session",
                Challenge = "YXV0aC1jaGFsbGVuZ2U",
                Type = "authentication",
                UserType = "b2b",
                Subject = "test-b2b-subject",
                RpId = "shop.example.com",
                ClientId = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            };

            _mockChallengeService.Setup(x => x.GetChallengeBySessionIdAsync("auth-session"))
                .ReturnsAsync(challenge);

            var unknownCredentialIdBytes = Encoding.UTF8.GetBytes("unknown-credential");
            var assertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = WebEncoders.Base64UrlEncode(unknownCredentialIdBytes),
                RawId = unknownCredentialIdBytes,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = Encoding.UTF8.GetBytes("auth-data"),
                    ClientDataJson = Encoding.UTF8.GetBytes("client-data"),
                    Signature = Encoding.UTF8.GetBytes("signature")
                }
            };

            var request = new IB2BPasskeyService.AuthenticationVerifyRequest
            {
                SessionId = "auth-session",
                ClientId = "test-client-id",
                AssertionResponse = assertionResponse
            };

            // Act
            var result = await _service.VerifyAuthenticationAsync(request);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Credential", result.ErrorMessage);
        }

        #endregion

        #region GetCredentialsBySubjectAsync Tests

        [Fact]
        public async Task GetCredentialsBySubjectAsync_WithCredentials_ShouldReturnList()
        {
            // Arrange
            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("cred-1"),
                    PublicKey = Encoding.UTF8.GetBytes("key-1"),
                    SignCount = 5,
                    DeviceName = "MacBook Pro",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                    LastUsedAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("cred-2"),
                    PublicKey = Encoding.UTF8.GetBytes("key-2"),
                    SignCount = 3,
                    DeviceName = "iPhone",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal", "hybrid" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-5)
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.GetCredentialsBySubjectAsync("test-b2b-subject");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Contains(result, c => c.DeviceName == "MacBook Pro");
            Assert.Contains(result, c => c.DeviceName == "iPhone");
        }

        [Fact]
        public async Task GetCredentialsBySubjectAsync_NoCredentials_ShouldReturnEmptyList()
        {
            // Act
            var result = await _service.GetCredentialsBySubjectAsync("user-without-credentials");

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetCredentialsBySubjectAsync_InvalidSubject_ShouldReturnEmptyList(string? subject)
        {
            // Act
            var result = await _service.GetCredentialsBySubjectAsync(subject!);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        #endregion

        #region DeleteCredentialAsync Tests

        [Fact]
        public async Task DeleteCredentialAsync_ExistingCredential_ShouldDeleteAndReturnTrue()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("delete-cred");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = "test-b2b-subject",
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act
            var result = await _service.DeleteCredentialAsync("test-b2b-subject", credentialIdBase64);

            // Assert
            Assert.True(result);

            var deleted = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId);
            Assert.Null(deleted);
        }

        [Fact]
        public async Task DeleteCredentialAsync_NonExistingCredential_ShouldReturnFalse()
        {
            // Act
            var result = await _service.DeleteCredentialAsync("test-b2b-subject", "non-existing-cred-id");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task DeleteCredentialAsync_WrongSubject_ShouldReturnFalse()
        {
            // Arrange
            var credentialId = Encoding.UTF8.GetBytes("other-user-cred");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = "other-user-subject",
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key"),
                SignCount = 0,
                AaGuid = Guid.NewGuid()
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act
            var result = await _service.DeleteCredentialAsync("test-b2b-subject", credentialIdBase64);

            // Assert
            Assert.False(result);

            // 他のユーザーのクレデンシャルは削除されていない
            var notDeleted = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == "other-user-subject");
            Assert.NotNull(notDeleted);
        }

        #endregion

        #region CountCredentialsBySubjectAsync Tests

        [Fact]
        public async Task CountCredentialsBySubjectAsync_WithCredentials_ShouldReturnCount()
        {
            // Arrange
            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-1"),
                    PublicKey = Encoding.UTF8.GetBytes("key-1"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-2"),
                    PublicKey = Encoding.UTF8.GetBytes("key-2"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = "test-b2b-subject",
                    CredentialId = Encoding.UTF8.GetBytes("count-cred-3"),
                    PublicKey = Encoding.UTF8.GetBytes("key-3"),
                    SignCount = 0,
                    AaGuid = Guid.NewGuid()
                }
            };
            _context.B2BPasskeyCredentials.AddRange(credentials);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.CountCredentialsBySubjectAsync("test-b2b-subject");

            // Assert
            Assert.Equal(3, result);
        }

        [Fact]
        public async Task CountCredentialsBySubjectAsync_NoCredentials_ShouldReturnZero()
        {
            // Act
            var result = await _service.CountCredentialsBySubjectAsync("user-without-creds");

            // Assert
            Assert.Equal(0, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task CountCredentialsBySubjectAsync_InvalidSubject_ShouldReturnZero(string? subject)
        {
            // Act
            var result = await _service.CountCredentialsBySubjectAsync(subject!);

            // Assert
            Assert.Equal(0, result);
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
