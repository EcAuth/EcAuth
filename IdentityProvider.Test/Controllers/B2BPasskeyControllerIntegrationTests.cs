using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    /// <summary>
    /// B2BPasskeyController の統合テスト
    /// 実際のサービス層を使用し、コントローラー→サービス→DBの一連のフローをテスト
    ///
    /// 注: TokenServiceは現在B2C（EcAuthUser）向けのクエリフィルタを使用しているため、
    /// List/Delete操作のテストではモックを使用します。
    /// Registration/Authenticationフローは実サービスを使用します。
    /// </summary>
    public class B2BPasskeyControllerIntegrationTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly Mock<IFido2> _mockFido2;
        private readonly IWebAuthnChallengeService _challengeService;
        private readonly IB2BUserService _b2bUserService;
        private readonly IB2BPasskeyService _passkeyService;
        private readonly IAuthorizationCodeService _authCodeService;
        private readonly ITokenService _tokenService;
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<ILogger<B2BPasskeyController>> _mockControllerLogger;
        private readonly B2BPasskeyController _controller;
        private readonly B2BPasskeyController _controllerWithMockToken;
        private readonly MockTenantService _mockTenantService;
        private readonly Organization _organization;
        private readonly Client _client;

        public B2BPasskeyControllerIntegrationTests()
        {
            _mockTenantService = new MockTenantService();
            _context = TestDbContextHelper.CreateInMemoryContext(tenantService: _mockTenantService);
            _mockFido2 = new Mock<IFido2>();

            // 実際のサービスをインスタンス化
            var challengeLogger = new Mock<ILogger<WebAuthnChallengeService>>();
            _challengeService = new WebAuthnChallengeService(_context, challengeLogger.Object);

            var userLogger = new Mock<ILogger<B2BUserService>>();
            _b2bUserService = new B2BUserService(_context, userLogger.Object);

            var passkeyLogger = new Mock<ILogger<B2BPasskeyService>>();
            // Fido2ファクトリーパターンでモックを注入
            Func<Fido2Configuration, IFido2> fido2Factory = _ => _mockFido2.Object;
            _passkeyService = new B2BPasskeyService(
                _context,
                _mockFido2.Object,
                _challengeService,
                _b2bUserService,
                passkeyLogger.Object,
                fido2Factory);

            var authCodeLogger = new Mock<ILogger<AuthorizationCodeService>>();
            _authCodeService = new AuthorizationCodeService(_context, authCodeLogger.Object);

            var tokenLogger = new Mock<ILogger<TokenService>>();
            _tokenService = new TokenService(_context, tokenLogger.Object);

            _mockControllerLogger = new Mock<ILogger<B2BPasskeyController>>();
            _mockTokenService = new Mock<ITokenService>();

            // コントローラー初期化（実サービス使用）
            _controller = new B2BPasskeyController(
                _passkeyService,
                _authCodeService,
                _tokenService,
                _b2bUserService,
                _context,
                _mockControllerLogger.Object);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // List/Delete操作用コントローラー（モックTokenService使用）
            // 注: 現在のTokenServiceはB2C向けクエリフィルタを使用するため、
            // B2Bユーザーのトークン検証ではモックを使用
            _controllerWithMockToken = new B2BPasskeyController(
                _passkeyService,
                _authCodeService,
                _mockTokenService.Object,
                _b2bUserService,
                _context,
                _mockControllerLogger.Object);

            _controllerWithMockToken.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            // テナント・クライアントの基本セットアップ
            _organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "テスト組織",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(_organization);

            _client = new Client
            {
                Id = 1,
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
                AppName = "テストクライアント",
                OrganizationId = 1,
                AllowedRpIds = new List<string> { "shop.example.com", "admin.example.com" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Clients.Add(_client);

            // RedirectUri を追加
            var redirectUri = new RedirectUri
            {
                Uri = "https://shop.example.com/admin/ecauth/callback",
                ClientId = 1
            };
            _context.RedirectUris.Add(redirectUri);

            _context.SaveChanges();
        }

        #region Registration Flow Integration Tests

        [Fact]
        public async Task IntegrationTest_FullRegistrationFlow_ShouldSucceed()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("test-b2b-subject", "admin@example.com");

            var registerOptionsRequest = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = _client.ClientId,
                ClientSecret = _client.ClientSecret!,
                RpId = "shop.example.com",
                B2BSubject = testUser.Subject,
                DisplayName = "テスト管理者",
                DeviceName = "MacBook Pro"
            };

            // Fido2モックセットアップ
            var credentialCreateOptions = new CredentialCreateOptions
            {
                Challenge = Encoding.UTF8.GetBytes("test-challenge"),
                Rp = new PublicKeyCredentialRpEntity("shop.example.com", "テスト組織"),
                User = new Fido2User
                {
                    Id = Encoding.UTF8.GetBytes(testUser.Subject),
                    Name = testUser.ExternalId,
                    DisplayName = "テスト管理者"
                },
                PubKeyCredParams = PubKeyCredParam.Defaults
            };
            _mockFido2.Setup(x => x.RequestNewCredential(It.IsAny<RequestNewCredentialParams>()))
                .Returns(credentialCreateOptions);

            // Step 1: RegisterOptions
            var optionsResult = await _controller.RegisterOptions(registerOptionsRequest);
            var okOptionsResult = Assert.IsType<OkObjectResult>(optionsResult);
            var optionsResponse = okOptionsResult.Value;
            Assert.NotNull(optionsResponse);

            var sessionId = optionsResponse.GetType().GetProperty("session_id")?.GetValue(optionsResponse)?.ToString();
            Assert.NotNull(sessionId);

            // Step 2: RegisterVerify
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

            var makeCredentialResult = new RegisteredPublicKeyCredential
            {
                Id = credentialIdBytes,
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

            var registerVerifyRequest = new B2BPasskeyController.RegisterVerifyRequest
            {
                ClientId = _client.ClientId,
                ClientSecret = _client.ClientSecret!,
                SessionId = sessionId,
                Response = attestationResponse,
                DeviceName = "MacBook Pro"
            };

            var verifyResult = await _controller.RegisterVerify(registerVerifyRequest);

            // Assert
            var okVerifyResult = Assert.IsType<OkObjectResult>(verifyResult);
            var verifyResponse = okVerifyResult.Value;
            Assert.NotNull(verifyResponse);

            var success = verifyResponse.GetType().GetProperty("success")?.GetValue(verifyResponse);
            Assert.Equal(true, success);

            var returnedCredentialId = verifyResponse.GetType().GetProperty("credential_id")?.GetValue(verifyResponse);
            Assert.NotNull(returnedCredentialId);

            // DBに保存されていることを確認
            var savedCredential = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == testUser.Subject);
            Assert.NotNull(savedCredential);
            Assert.Equal("MacBook Pro", savedCredential.DeviceName);
        }

        [Fact]
        public async Task IntegrationTest_RegisterOptions_InvalidRpId_ReturnsBadRequest()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("test-b2b-subject-2", "admin2@example.com");

            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = _client.ClientId,
                ClientSecret = _client.ClientSecret!,
                RpId = "unauthorized.example.com", // AllowedRpIdsに含まれない
                B2BSubject = testUser.Subject,
                DisplayName = "テスト管理者"
            };

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);

            var errorDescription = response.GetType().GetProperty("error_description")?.GetValue(response)?.ToString();
            Assert.Contains("RpId", errorDescription);
        }

        [Fact]
        public async Task IntegrationTest_RegisterVerify_ExpiredSession_ReturnsBadRequest()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("test-b2b-subject-3", "admin3@example.com");

            // 期限切れのチャレンジを直接作成
            var expiredChallenge = new WebAuthnChallenge
            {
                SessionId = "expired-session-id",
                Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("expired-challenge")),
                Type = "registration",
                UserType = "b2b",
                Subject = testUser.Subject,
                RpId = "shop.example.com",
                ClientId = _client.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), // 期限切れ
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6)
            };
            _context.WebAuthnChallenges.Add(expiredChallenge);
            await _context.SaveChangesAsync();

            var request = new B2BPasskeyController.RegisterVerifyRequest
            {
                ClientId = _client.ClientId,
                ClientSecret = _client.ClientSecret!,
                SessionId = "expired-session-id",
                Response = new AuthenticatorAttestationRawResponse(),
                DeviceName = "MacBook Pro"
            };

            // Act
            var result = await _controller.RegisterVerify(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);
        }

        #endregion

        #region Authentication Flow Integration Tests

        [Fact]
        public async Task IntegrationTest_FullAuthenticationFlow_ShouldSucceed()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("auth-test-subject", "authuser@example.com");
            var credentialIdBytes = Encoding.UTF8.GetBytes("auth-credential-id");

            // パスキーを事前登録
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = testUser.Subject,
                CredentialId = credentialIdBytes,
                PublicKey = Encoding.UTF8.GetBytes("public-key"),
                SignCount = 5,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                Transports = new[] { "internal" },
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            // Step 1: AuthenticateOptions
            var authenticateOptionsRequest = new B2BPasskeyController.AuthenticateOptionsRequest
            {
                ClientId = _client.ClientId,
                RpId = "shop.example.com",
                B2BSubject = testUser.Subject
            };

            var optionsResult = await _controller.AuthenticateOptions(authenticateOptionsRequest);
            var okOptionsResult = Assert.IsType<OkObjectResult>(optionsResult);
            var optionsResponse = okOptionsResult.Value;
            Assert.NotNull(optionsResponse);

            var sessionId = optionsResponse.GetType().GetProperty("session_id")?.GetValue(optionsResponse)?.ToString();
            Assert.NotNull(sessionId);

            // Step 2: AuthenticateVerify
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

            var verifyAssertionResult = new VerifyAssertionResult
            {
                SignCount = 6
            };

            _mockFido2.Setup(x => x.MakeAssertionAsync(
                It.IsAny<MakeAssertionParams>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(verifyAssertionResult);

            var authenticateVerifyRequest = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = _client.ClientId,
                SessionId = sessionId,
                RedirectUri = "https://shop.example.com/admin/ecauth/callback",
                State = "test-state",
                Response = assertionResponse
            };

            var verifyResult = await _controller.AuthenticateVerify(authenticateVerifyRequest);

            // Assert
            var okVerifyResult = Assert.IsType<OkObjectResult>(verifyResult);
            var verifyResponse = okVerifyResult.Value;
            Assert.NotNull(verifyResponse);

            var redirectUrl = verifyResponse.GetType().GetProperty("redirect_url")?.GetValue(verifyResponse)?.ToString();
            Assert.NotNull(redirectUrl);
            Assert.Contains("code=", redirectUrl);
            Assert.Contains("state=test-state", redirectUrl);

            // SignCountが更新されていることを確認
            var updatedCredential = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == testUser.Subject);
            Assert.NotNull(updatedCredential);
            Assert.Equal(6u, updatedCredential.SignCount);
            Assert.NotNull(updatedCredential.LastUsedAt);

            // 認可コードがDBに保存されていることを確認（B2B認証はB2BSubjectに設定される）
            var savedAuthCode = await _context.AuthorizationCodes
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.B2BSubject == testUser.Subject);
            Assert.NotNull(savedAuthCode);
            Assert.NotNull(savedAuthCode.B2BSubject);
        }

        [Fact]
        public async Task IntegrationTest_AuthenticateVerify_InvalidRedirectUri_ReturnsBadRequest()
        {
            // Arrange
            var authenticateVerifyRequest = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = _client.ClientId,
                SessionId = "some-session",
                RedirectUri = "https://malicious.example.com/callback", // 未登録のURI
                Response = new AuthenticatorAssertionRawResponse()
            };

            // Act
            var result = await _controller.AuthenticateVerify(authenticateVerifyRequest);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);

            var errorDescription = response.GetType().GetProperty("error_description")?.GetValue(response)?.ToString();
            Assert.Contains("redirect_uri", errorDescription);
        }

        [Fact]
        public async Task IntegrationTest_AuthenticateVerify_CredentialNotFound_ReturnsBadRequest()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("no-credential-user", "nopasskey@example.com");

            // チャレンジを作成
            var challenge = new WebAuthnChallenge
            {
                SessionId = "auth-session-no-cred",
                Challenge = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("challenge")),
                Type = "authentication",
                UserType = "b2b",
                Subject = testUser.Subject,
                RpId = "shop.example.com",
                ClientId = _client.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.WebAuthnChallenges.Add(challenge);
            await _context.SaveChangesAsync();

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

            var authenticateVerifyRequest = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = _client.ClientId,
                SessionId = "auth-session-no-cred",
                RedirectUri = "https://shop.example.com/admin/ecauth/callback",
                Response = assertionResponse
            };

            // Act
            var result = await _controller.AuthenticateVerify(authenticateVerifyRequest);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);
        }

        #endregion

        #region List/Delete Integration Tests

        /// <summary>
        /// List操作の統合テスト
        /// 注: TokenServiceは現在B2C向けクエリフィルタを使用するため、モックを使用
        /// </summary>
        [Fact]
        public async Task IntegrationTest_ListPasskeys_WithValidToken_ReturnsPasskeyList()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("list-test-subject", "listuser@example.com");

            // パスキーを登録
            var credentials = new[]
            {
                new B2BPasskeyCredential
                {
                    B2BSubject = testUser.Subject,
                    CredentialId = Encoding.UTF8.GetBytes("list-cred-1"),
                    PublicKey = Encoding.UTF8.GetBytes("key-1"),
                    SignCount = 5,
                    DeviceName = "MacBook Pro",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
                    LastUsedAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new B2BPasskeyCredential
                {
                    B2BSubject = testUser.Subject,
                    CredentialId = Encoding.UTF8.GetBytes("list-cred-2"),
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

            // モックTokenServiceセットアップ
            var accessToken = "list-test-access-token";
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(testUser.Subject);

            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controllerWithMockToken.List();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var passkeys = response.GetType().GetProperty("passkeys")?.GetValue(response);
            Assert.NotNull(passkeys);

            // IEnumerableとしてカウント
            var passkeysEnumerable = passkeys as System.Collections.IEnumerable;
            Assert.NotNull(passkeysEnumerable);
            var count = passkeysEnumerable.Cast<object>().Count();
            Assert.Equal(2, count);
        }

        /// <summary>
        /// Delete操作の統合テスト
        /// 注: TokenServiceは現在B2C向けクエリフィルタを使用するため、モックを使用
        /// </summary>
        [Fact]
        public async Task IntegrationTest_DeletePasskey_WithValidToken_ReturnsNoContent()
        {
            // Arrange
            var testUser = await CreateTestB2BUserAsync("delete-test-subject", "deleteuser@example.com");

            var credentialId = Encoding.UTF8.GetBytes("delete-cred");
            var credential = new B2BPasskeyCredential
            {
                B2BSubject = testUser.Subject,
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key"),
                SignCount = 0,
                DeviceName = "MacBook Pro",
                AaGuid = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BPasskeyCredentials.Add(credential);
            await _context.SaveChangesAsync();

            // モックTokenServiceセットアップ
            var accessToken = "delete-test-access-token";
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(testUser.Subject);

            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act
            var result = await _controllerWithMockToken.Delete(credentialIdBase64);

            // Assert
            Assert.IsType<NoContentResult>(result);

            // DBから削除されていることを確認
            var deletedCredential = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId);
            Assert.Null(deletedCredential);
        }

        /// <summary>
        /// 期限切れトークンのテスト
        /// 注: TokenServiceは現在B2C向けクエリフィルタを使用するため、モックを使用
        /// </summary>
        [Fact]
        public async Task IntegrationTest_ListPasskeys_ExpiredToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "expired-access-token";

            // モックTokenServiceセットアップ: 期限切れトークンはnullを返す
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync((string?)null);

            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controllerWithMockToken.List();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", error);
        }

        #endregion

        #region Multi-Tenant Integration Tests

        /// <summary>
        /// マルチテナント: 異なるOrganization間でデータが分離されることを確認
        /// 注: TokenServiceは現在B2C向けクエリフィルタを使用するため、モックを使用
        /// </summary>
        [Fact]
        public async Task IntegrationTest_MultiTenant_DifferentOrganizations_DataIsolation()
        {
            // Arrange: 2つ目のOrganizationとClientを作成
            var org2 = new Organization
            {
                Id = 2,
                Code = "org2-code",
                Name = "第二組織",
                TenantName = "org2-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(org2);

            var client2 = new Client
            {
                Id = 2,
                ClientId = "client2-id",
                ClientSecret = "client2-secret",
                AppName = "第二クライアント",
                OrganizationId = 2,
                AllowedRpIds = new List<string> { "shop2.example.com" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Clients.Add(client2);

            var redirectUri2 = new RedirectUri
            {
                Uri = "https://shop2.example.com/admin/ecauth/callback",
                ClientId = 2
            };
            _context.RedirectUris.Add(redirectUri2);
            await _context.SaveChangesAsync();

            // Organization 1 のユーザー
            var user1 = await CreateTestB2BUserAsync("tenant1-user", "user1@org1.com", 1);

            // Organization 2 のユーザー
            var user2 = new B2BUser
            {
                Subject = "tenant2-user",
                ExternalId = "user2@org2.com",
                UserType = "admin",
                OrganizationId = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BUsers.Add(user2);

            // 各テナントにパスキーを登録
            var cred1 = new B2BPasskeyCredential
            {
                B2BSubject = user1.Subject,
                CredentialId = Encoding.UTF8.GetBytes("tenant1-cred"),
                PublicKey = Encoding.UTF8.GetBytes("key-1"),
                SignCount = 0,
                DeviceName = "Org1 Device",
                AaGuid = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BPasskeyCredentials.Add(cred1);

            var cred2 = new B2BPasskeyCredential
            {
                B2BSubject = user2.Subject,
                CredentialId = Encoding.UTF8.GetBytes("tenant2-cred"),
                PublicKey = Encoding.UTF8.GetBytes("key-2"),
                SignCount = 0,
                DeviceName = "Org2 Device",
                AaGuid = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BPasskeyCredentials.Add(cred2);
            await _context.SaveChangesAsync();

            // モックTokenServiceセットアップ
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync("tenant1-token"))
                .ReturnsAsync(user1.Subject);
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync("tenant2-token"))
                .ReturnsAsync(user2.Subject);

            // Act: Tenant 1のパスキー一覧取得
            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = "Bearer tenant1-token";
            var result1 = await _controllerWithMockToken.List();

            var okResult1 = Assert.IsType<OkObjectResult>(result1);
            var response1 = okResult1.Value;
            var passkeys1 = response1?.GetType().GetProperty("passkeys")?.GetValue(response1) as System.Collections.IEnumerable;
            Assert.NotNull(passkeys1);

            // Tenant 1のパスキーのみが返されることを確認
            var passkeys1List = passkeys1.Cast<object>().ToList();
            Assert.Single(passkeys1List);
            var deviceName1 = passkeys1List[0].GetType().GetProperty("device_name")?.GetValue(passkeys1List[0]);
            Assert.Equal("Org1 Device", deviceName1);

            // Act: Tenant 2のパスキー一覧取得
            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = "Bearer tenant2-token";
            var result2 = await _controllerWithMockToken.List();

            var okResult2 = Assert.IsType<OkObjectResult>(result2);
            var response2 = okResult2.Value;
            var passkeys2 = response2?.GetType().GetProperty("passkeys")?.GetValue(response2) as System.Collections.IEnumerable;
            Assert.NotNull(passkeys2);

            // Tenant 2のパスキーのみが返されることを確認
            var passkeys2List = passkeys2.Cast<object>().ToList();
            Assert.Single(passkeys2List);
            var deviceName2 = passkeys2List[0].GetType().GetProperty("device_name")?.GetValue(passkeys2List[0]);
            Assert.Equal("Org2 Device", deviceName2);
        }

        /// <summary>
        /// クロステナントアクセス: 他テナントのパスキーを削除できないことを確認
        /// 注: TokenServiceは現在B2C向けクエリフィルタを使用するため、モックを使用
        /// </summary>
        [Fact]
        public async Task IntegrationTest_CrossTenantAccess_CannotDeleteOtherTenantCredential()
        {
            // Arrange
            var org2 = new Organization
            {
                Id = 3,
                Code = "org3-code",
                Name = "第三組織",
                TenantName = "org3-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(org2);
            await _context.SaveChangesAsync();

            // Organization 1 のユーザー
            var user1 = await CreateTestB2BUserAsync("cross-tenant-user1", "crossuser1@org1.com", 1);

            // Organization 3 のユーザー
            var user3 = new B2BUser
            {
                Subject = "cross-tenant-user3",
                ExternalId = "crossuser3@org3.com",
                UserType = "admin",
                OrganizationId = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BUsers.Add(user3);

            // Organization 3 のパスキー
            var credentialId = Encoding.UTF8.GetBytes("org3-credential");
            var cred3 = new B2BPasskeyCredential
            {
                B2BSubject = user3.Subject,
                CredentialId = credentialId,
                PublicKey = Encoding.UTF8.GetBytes("key-3"),
                SignCount = 0,
                DeviceName = "Org3 Device",
                AaGuid = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BPasskeyCredentials.Add(cred3);
            await _context.SaveChangesAsync();

            // モックTokenServiceセットアップ: Organization 1 のユーザーのトークン
            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync("cross-tenant-token1"))
                .ReturnsAsync(user1.Subject);

            _controllerWithMockToken.HttpContext.Request.Headers["Authorization"] = "Bearer cross-tenant-token1";

            var credentialIdBase64 = WebEncoders.Base64UrlEncode(credentialId);

            // Act: Organization 1 のユーザーが Organization 3 のパスキーを削除しようとする
            var result = await _controllerWithMockToken.Delete(credentialIdBase64);

            // Assert: 削除失敗（NotFound）
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var response = notFoundResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("not_found", error);

            // パスキーがまだ存在することを確認
            var stillExists = await _context.B2BPasskeyCredentials
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId);
            Assert.NotNull(stillExists);
        }

        #endregion

        #region Helper Methods

        private async Task<B2BUser> CreateTestB2BUserAsync(string subject, string externalId, int organizationId = 1)
        {
            var user = new B2BUser
            {
                Subject = subject,
                ExternalId = externalId,
                UserType = "admin",
                OrganizationId = organizationId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();
            return user;
        }

        #endregion

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
