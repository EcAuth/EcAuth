using Fido2NetLib;
using Fido2NetLib.Objects;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static IdentityProvider.Services.IB2BPasskeyService;

namespace IdentityProvider.Test.Controllers
{
    public class B2BPasskeyControllerTests
    {
        private readonly Mock<IB2BPasskeyService> _mockPasskeyService;
        private readonly Mock<IAuthorizationCodeService> _mockAuthCodeService;
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<IB2BUserService> _mockB2BUserService;
        private readonly Mock<ILogger<B2BPasskeyController>> _mockLogger;
        private readonly EcAuthDbContext _context;
        private readonly B2BPasskeyController _controller;

        public B2BPasskeyControllerTests()
        {
            _mockPasskeyService = new Mock<IB2BPasskeyService>();
            _mockAuthCodeService = new Mock<IAuthorizationCodeService>();
            _mockTokenService = new Mock<ITokenService>();
            _mockB2BUserService = new Mock<IB2BUserService>();
            _mockLogger = new Mock<ILogger<B2BPasskeyController>>();

            // InMemory DbContext
            var options = new DbContextOptionsBuilder<EcAuthDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var mockTenantService = new Mock<ITenantService>();
            mockTenantService.Setup(x => x.TenantName).Returns("test-tenant");

            _context = new EcAuthDbContext(options, mockTenantService.Object);

            _controller = new B2BPasskeyController(
                _mockPasskeyService.Object,
                _mockAuthCodeService.Object,
                _mockTokenService.Object,
                _mockB2BUserService.Object,
                _context,
                _mockLogger.Object);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        #region RegisterOptions Tests

        [Fact]
        public async Task RegisterOptions_ValidRequest_ReturnsSessionIdAndOptions()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret!,
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000",
                DisplayName = "Test User",
                DeviceName = "MacBook Pro"
            };

            var expectedResult = new RegistrationOptionsResult
            {
                SessionId = "test-session-id",
                Options = CreateMockCredentialCreateOptions()
            };

            _mockPasskeyService.Setup(x => x.CreateRegistrationOptionsAsync(It.IsAny<RegistrationOptionsRequest>()))
                .ReturnsAsync(expectedResult);

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var sessionId = response.GetType().GetProperty("session_id")?.GetValue(response);
            Assert.Equal("test-session-id", sessionId);
        }

        [Fact]
        public async Task RegisterOptions_InvalidClientSecret_ReturnsUnauthorized()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = client.ClientId,
                ClientSecret = "wrong-secret",
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000"
            };

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_client", error);
        }

        [Fact]
        public async Task RegisterOptions_ClientNotFound_ReturnsUnauthorized()
        {
            // Arrange
            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = "nonexistent-client",
                ClientSecret = "secret",
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000"
            };

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_client", error);
        }

        [Fact]
        public async Task RegisterOptions_MissingClientId_ReturnsBadRequest()
        {
            // Arrange
            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = "",
                ClientSecret = "secret",
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000"
            };

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);
        }

        [Fact]
        public async Task RegisterOptions_ServiceThrowsException_ReturnsInternalServerError()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.RegisterOptionsRequest
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret!,
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000"
            };

            _mockPasskeyService.Setup(x => x.CreateRegistrationOptionsAsync(It.IsAny<RegistrationOptionsRequest>()))
                .ThrowsAsync(new Exception("Service error"));

            // Act
            var result = await _controller.RegisterOptions(request);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);

            var response = statusCodeResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("server_error", error);
        }

        #endregion

        #region RegisterVerify Tests

        [Fact]
        public async Task RegisterVerify_ValidRequest_ReturnsSuccess()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.RegisterVerifyRequest
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret!,
                SessionId = "test-session-id",
                Response = new AuthenticatorAttestationRawResponse(),
                DeviceName = "MacBook Pro"
            };

            var expectedResult = new RegistrationVerifyResult
            {
                Success = true,
                CredentialId = "credential-id-base64url"
            };

            _mockPasskeyService.Setup(x => x.VerifyRegistrationAsync(It.IsAny<RegistrationVerifyRequest>()))
                .ReturnsAsync(expectedResult);

            // Act
            var result = await _controller.RegisterVerify(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var success = response.GetType().GetProperty("success")?.GetValue(response);
            Assert.Equal(true, success);

            var credentialId = response.GetType().GetProperty("credential_id")?.GetValue(response);
            Assert.Equal("credential-id-base64url", credentialId);
        }

        [Fact]
        public async Task RegisterVerify_VerificationFailed_ReturnsBadRequest()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.RegisterVerifyRequest
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret!,
                SessionId = "test-session-id",
                Response = new AuthenticatorAttestationRawResponse()
            };

            var expectedResult = new RegistrationVerifyResult
            {
                Success = false,
                ErrorMessage = "Invalid attestation"
            };

            _mockPasskeyService.Setup(x => x.VerifyRegistrationAsync(It.IsAny<RegistrationVerifyRequest>()))
                .ReturnsAsync(expectedResult);

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

        #region AuthenticateOptions Tests

        [Fact]
        public async Task AuthenticateOptions_ValidRequest_ReturnsSessionIdAndOptions()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.AuthenticateOptionsRequest
            {
                ClientId = client.ClientId,
                RpId = "shop.example.com",
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000"
            };

            var expectedResult = new AuthenticationOptionsResult
            {
                SessionId = "test-session-id",
                Options = CreateMockAssertionOptions()
            };

            _mockPasskeyService.Setup(x => x.CreateAuthenticationOptionsAsync(It.IsAny<AuthenticationOptionsRequest>()))
                .ReturnsAsync(expectedResult);

            // Act
            var result = await _controller.AuthenticateOptions(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var sessionId = response.GetType().GetProperty("session_id")?.GetValue(response);
            Assert.Equal("test-session-id", sessionId);
        }

        [Fact]
        public async Task AuthenticateOptions_ClientNotFound_ReturnsUnauthorized()
        {
            // Arrange
            var request = new B2BPasskeyController.AuthenticateOptionsRequest
            {
                ClientId = "nonexistent-client",
                RpId = "shop.example.com"
            };

            // Act
            var result = await _controller.AuthenticateOptions(request);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_client", error);
        }

        #endregion

        #region AuthenticateVerify Tests

        [Fact]
        public async Task AuthenticateVerify_ValidRequest_ReturnsRedirectUrl()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = client.ClientId,
                SessionId = "test-session-id",
                RedirectUri = "https://shop.example.com/admin/ecauth/callback",
                State = "test-state",
                Response = new AuthenticatorAssertionRawResponse()
            };

            var verifyResult = new AuthenticationVerifyResult
            {
                Success = true,
                B2BSubject = "550e8400-e29b-41d4-a716-446655440000",
                CredentialId = "credential-id"
            };

            var authCode = new AuthorizationCode
            {
                Code = "test-auth-code",
                Subject = "550e8400-e29b-41d4-a716-446655440000",
                SubjectType = SubjectType.B2B,
                ClientId = client.Id,
                RedirectUri = request.RedirectUri,
                State = request.State,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            };

            _mockPasskeyService.Setup(x => x.VerifyAuthenticationAsync(It.IsAny<AuthenticationVerifyRequest>()))
                .ReturnsAsync(verifyResult);

            _mockAuthCodeService.Setup(x => x.GenerateAuthorizationCodeAsync(It.IsAny<IAuthorizationCodeService.AuthorizationCodeRequest>()))
                .ReturnsAsync(authCode);

            // Act
            var result = await _controller.AuthenticateVerify(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var redirectUrl = response.GetType().GetProperty("redirect_url")?.GetValue(response)?.ToString();
            Assert.NotNull(redirectUrl);
            Assert.Contains("code=test-auth-code", redirectUrl);
            Assert.Contains("state=test-state", redirectUrl);
        }

        [Fact]
        public async Task AuthenticateVerify_AuthenticationFailed_ReturnsBadRequest()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = client.ClientId,
                SessionId = "test-session-id",
                RedirectUri = "https://shop.example.com/admin/ecauth/callback",
                Response = new AuthenticatorAssertionRawResponse()
            };

            var verifyResult = new AuthenticationVerifyResult
            {
                Success = false,
                ErrorMessage = "Invalid assertion"
            };

            _mockPasskeyService.Setup(x => x.VerifyAuthenticationAsync(It.IsAny<AuthenticationVerifyRequest>()))
                .ReturnsAsync(verifyResult);

            // Act
            var result = await _controller.AuthenticateVerify(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);
        }

        [Fact]
        public async Task AuthenticateVerify_InvalidRedirectUri_ReturnsBadRequest()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = client.ClientId,
                SessionId = "test-session-id",
                RedirectUri = "https://malicious.example.com/callback",
                Response = new AuthenticatorAssertionRawResponse()
            };

            // Act
            var result = await _controller.AuthenticateVerify(request);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);
        }

        [Fact]
        public async Task AuthenticateVerify_ClientWithNoRedirectUris_ReturnsBadRequest()
        {
            // Arrange - RedirectUri を設定しないクライアントを作成
            var organization = new Organization
            {
                Code = "test-org-no-redirect",
                Name = "Test Organization No Redirect",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);
            await _context.SaveChangesAsync();

            var clientWithNoRedirect = new Client
            {
                ClientId = "client-no-redirect",
                ClientSecret = "test-client-secret",
                AppName = "Test App No Redirect",
                OrganizationId = organization.Id,
                AllowedRpIds = new List<string> { "shop.example.com" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Clients.Add(clientWithNoRedirect);
            await _context.SaveChangesAsync();
            // RedirectUri は追加しない

            var request = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = clientWithNoRedirect.ClientId,
                SessionId = "test-session-id",
                RedirectUri = "https://shop.example.com/admin/ecauth/callback",
                Response = new AuthenticatorAssertionRawResponse()
            };

            // Act
            var result = await _controller.AuthenticateVerify(request);

            // Assert - RedirectUri が設定されていないため、検証失敗
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", error);

            var errorDescription = response.GetType().GetProperty("error_description")?.GetValue(response);
            Assert.Equal("redirect_uri が許可されていません。", errorDescription);
        }

        [Fact]
        public async Task AuthenticateVerify_ValidRedirectUri_PassesRedirectUriValidation()
        {
            // Arrange
            var client = await CreateTestClientAsync();

            var request = new B2BPasskeyController.AuthenticateVerifyRequest
            {
                ClientId = client.ClientId,
                SessionId = "test-session-id",
                RedirectUri = "https://shop.example.com/admin/ecauth/callback", // 許可されたURI
                State = "test-state",
                Response = new AuthenticatorAssertionRawResponse()
            };

            // サービスモックを設定（redirect_uri検証後にサービスエラーになる）
            _mockPasskeyService.Setup(x => x.VerifyAuthenticationAsync(It.IsAny<AuthenticationVerifyRequest>()))
                .ReturnsAsync(new AuthenticationVerifyResult
                {
                    Success = false,
                    ErrorMessage = "Challenge not found" // redirect_uri検証は通過したことを示す
                });

            // Act
            var result = await _controller.AuthenticateVerify(request);

            // Assert - redirect_uri検証は通過し、サービスエラー（Challenge not found）が返る
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var errorDescription = response.GetType().GetProperty("error_description")?.GetValue(response);
            Assert.Equal("Challenge not found", errorDescription);
        }

        #endregion

        #region List Tests

        [Fact]
        public async Task List_ValidBearerToken_ReturnsPasskeyList()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "550e8400-e29b-41d4-a716-446655440000";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            var passkeys = new List<PasskeyInfo>
            {
                new PasskeyInfo
                {
                    CredentialId = "credential-1",
                    DeviceName = "MacBook Pro",
                    AaGuid = Guid.NewGuid(),
                    Transports = new[] { "internal" },
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-7),
                    LastUsedAt = DateTimeOffset.UtcNow
                }
            };

            _mockPasskeyService.Setup(x => x.GetCredentialsBySubjectAsync(subject))
                .ReturnsAsync(passkeys);

            // Act
            var result = await _controller.List();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;
            Assert.NotNull(response);

            var passkeysProperty = response.GetType().GetProperty("passkeys")?.GetValue(response);
            Assert.NotNull(passkeysProperty);
        }

        [Fact]
        public async Task List_MissingAuthorizationHeader_ReturnsUnauthorized()
        {
            // Arrange
            // No Authorization header

            // Act
            var result = await _controller.List();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", error);
        }

        [Fact]
        public async Task List_InvalidAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "invalid-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _controller.List();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", error);
        }

        #endregion

        #region Delete Tests

        [Fact]
        public async Task Delete_ValidRequest_ReturnsNoContent()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "550e8400-e29b-41d4-a716-446655440000";
            var credentialId = "credential-id-base64url";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockPasskeyService.Setup(x => x.DeleteCredentialAsync(subject, credentialId))
                .ReturnsAsync(true);

            // Act
            var result = await _controller.Delete(credentialId);

            // Assert
            Assert.IsType<NoContentResult>(result);
        }

        [Fact]
        public async Task Delete_CredentialNotFound_ReturnsNotFound()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "550e8400-e29b-41d4-a716-446655440000";
            var credentialId = "nonexistent-credential";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockPasskeyService.Setup(x => x.DeleteCredentialAsync(subject, credentialId))
                .ReturnsAsync(false);

            // Act
            var result = await _controller.Delete(credentialId);

            // Assert
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var response = notFoundResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("not_found", error);
        }

        [Fact]
        public async Task Delete_MissingAuthorizationHeader_ReturnsUnauthorized()
        {
            // Arrange
            var credentialId = "credential-id";
            // No Authorization header

            // Act
            var result = await _controller.Delete(credentialId);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var error = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", error);
        }

        #endregion

        #region Helper Methods

        private async Task<Client> CreateTestClientAsync()
        {
            var organization = new Organization
            {
                Code = "test-org",
                Name = "Test Organization",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);
            await _context.SaveChangesAsync();

            var client = new Client
            {
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
                AppName = "Test App",
                OrganizationId = organization.Id,
                AllowedRpIds = new List<string> { "shop.example.com" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Clients.Add(client);
            await _context.SaveChangesAsync();

            // RedirectUri を追加
            var redirectUri = new RedirectUri
            {
                Uri = "https://shop.example.com/admin/ecauth/callback",
                ClientId = client.Id
            };
            _context.RedirectUris.Add(redirectUri);
            await _context.SaveChangesAsync();

            return client;
        }

        private static CredentialCreateOptions CreateMockCredentialCreateOptions()
        {
            return new CredentialCreateOptions
            {
                Challenge = new byte[] { 1, 2, 3, 4 },
                Rp = new PublicKeyCredentialRpEntity("shop.example.com", "Test Shop", null),
                User = new Fido2User
                {
                    Id = new byte[] { 1, 2, 3 },
                    Name = "test@example.com",
                    DisplayName = "Test User"
                },
                PubKeyCredParams = new List<PubKeyCredParam>
                {
                    new PubKeyCredParam(COSE.Algorithm.ES256)
                },
                Timeout = 60000,
                Attestation = AttestationConveyancePreference.None,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred
                }
            };
        }

        private static AssertionOptions CreateMockAssertionOptions()
        {
            return new AssertionOptions
            {
                Challenge = new byte[] { 1, 2, 3, 4 },
                RpId = "shop.example.com",
                AllowCredentials = new List<PublicKeyCredentialDescriptor>
                {
                    new PublicKeyCredentialDescriptor(new byte[] { 5, 6, 7, 8 })
                },
                UserVerification = UserVerificationRequirement.Preferred,
                Timeout = 60000
            };
        }

        #endregion
    }
}
