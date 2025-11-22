using IdentityProvider.Controllers;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class ExternalUserInfoControllerTests
    {
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<IExternalUserInfoService> _mockExternalUserInfoService;
        private readonly Mock<ILogger<ExternalUserInfoController>> _mockLogger;
        private readonly ExternalUserInfoController _controller;

        public ExternalUserInfoControllerTests()
        {
            _mockTokenService = new Mock<ITokenService>();
            _mockExternalUserInfoService = new Mock<IExternalUserInfoService>();
            _mockLogger = new Mock<ILogger<ExternalUserInfoController>>();

            _controller = new ExternalUserInfoController(
                _mockTokenService.Object,
                _mockExternalUserInfoService.Object,
                _mockLogger.Object);

            // HttpContext のセットアップ
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Fact]
        public async Task GetExternalUserInfo_ValidRequest_ReturnsExternalUserInfo()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var provider = "google-oauth2";

            var externalUserInfo = new IExternalUserInfoService.ExternalUserInfo
            {
                UserInfoClaims = JsonDocument.Parse(@"{
                    ""sub"": ""google-user-123"",
                    ""email"": ""test@example.com"",
                    ""name"": ""Test User""
                }"),
                ExternalProvider = provider
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.Is<IExternalUserInfoService.GetExternalUserInfoRequest>(r =>
                    r.EcAuthSubject == subject && r.ExternalProvider == provider)))
                .ReturnsAsync(externalUserInfo);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value as Dictionary<string, object>;
            Assert.NotNull(response);

            Assert.Equal("google-user-123", response["sub"]);
            Assert.Equal("test@example.com", response["email"]);
            Assert.Equal("Test User", response["name"]);
            Assert.Equal(provider, response["provider"]);
        }

        [Fact]
        public async Task GetExternalUserInfo_MissingProvider_ReturnsBadRequest()
        {
            // Arrange
            var accessToken = "valid-access-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.GetExternalUserInfo(null);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_EmptyProvider_ReturnsBadRequest()
        {
            // Arrange
            var accessToken = "valid-access-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.GetExternalUserInfo("");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_request", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_MissingAuthorizationHeader_ReturnsUnauthorized()
        {
            // Arrange
            var provider = "google-oauth2";
            // Authorization ヘッダーを設定しない

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_InvalidAuthorizationHeaderFormat_ReturnsUnauthorized()
        {
            // Arrange
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = "Invalid Header Format";

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_NonBearerAuthenticationScheme_ReturnsUnauthorized()
        {
            // Arrange
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = "Basic dGVzdDpwYXNzd29yZA==";

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_EmptyAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = "Bearer ";

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_InvalidAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "invalid-access-token";
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_ExpiredAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "expired-access-token";
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync((string?)null); // 期限切れの場合はnullが返される

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_UserInfoNotFound_ReturnsNotFound()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var provider = "google-oauth2";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.IsAny<IExternalUserInfoService.GetExternalUserInfoRequest>()))
                .ReturnsAsync((IExternalUserInfoService.ExternalUserInfo?)null);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            var response = notFoundResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("not_found", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_TokenServiceThrowsException_ReturnsInternalServerError()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var provider = "google-oauth2";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ThrowsAsync(new Exception("Database connection failed"));

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);

            var response = statusCodeResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("server_error", errorProperty);
        }

        [Fact]
        public async Task GetExternalUserInfo_ExternalUserInfoServiceThrowsException_ReturnsInternalServerError()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var provider = "google-oauth2";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.IsAny<IExternalUserInfoService.GetExternalUserInfoRequest>()))
                .ThrowsAsync(new Exception("External IdP service failed"));

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);

            var response = statusCodeResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("server_error", errorProperty);
        }

        [Theory]
        [InlineData("google-oauth2")]
        [InlineData("federate-oauth2")]
        [InlineData("line-oauth2")]
        public async Task GetExternalUserInfo_DifferentProviders_ReturnsCorrectProviderInResponse(string provider)
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";

            var externalUserInfo = new IExternalUserInfoService.ExternalUserInfo
            {
                UserInfoClaims = JsonDocument.Parse(@"{""sub"": ""external-user""}"),
                ExternalProvider = provider
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.Is<IExternalUserInfoService.GetExternalUserInfoRequest>(r =>
                    r.ExternalProvider == provider)))
                .ReturnsAsync(externalUserInfo);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value as Dictionary<string, object>;
            Assert.NotNull(response);
            Assert.Equal(provider, response["provider"]);
        }

        [Fact]
        public async Task GetExternalUserInfo_ComplexUserInfoJson_ConvertsCorrectly()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var provider = "google-oauth2";

            var externalUserInfo = new IExternalUserInfoService.ExternalUserInfo
            {
                UserInfoClaims = JsonDocument.Parse(@"{
                    ""sub"": ""google-user-123"",
                    ""email"": ""test@example.com"",
                    ""name"": ""Test User"",
                    ""picture"": ""https://example.com/photo.jpg"",
                    ""email_verified"": true,
                    ""locale"": ""ja"",
                    ""custom_field"": null,
                    ""roles"": [""admin"", ""user""],
                    ""metadata"": {
                        ""created_at"": ""2024-01-01"",
                        ""login_count"": 42
                    }
                }"),
                ExternalProvider = provider
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.IsAny<IExternalUserInfoService.GetExternalUserInfoRequest>()))
                .ReturnsAsync(externalUserInfo);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value as Dictionary<string, object>;
            Assert.NotNull(response);

            // Verify basic fields
            Assert.Equal("google-user-123", response["sub"]);
            Assert.Equal("test@example.com", response["email"]);
            Assert.Equal("Test User", response["name"]);
            Assert.Equal(true, response["email_verified"]);

            // Verify array
            var roles = response["roles"] as object[];
            Assert.NotNull(roles);
            Assert.Equal(2, roles.Length);
            Assert.Equal("admin", roles[0]);

            // Verify nested object
            var metadata = response["metadata"] as Dictionary<string, object>;
            Assert.NotNull(metadata);
            Assert.Equal("2024-01-01", metadata["created_at"]);
            // JSON numbers are deserialized as double by default
            Assert.Equal(42.0, metadata["login_count"]);

            // Verify provider field is added
            Assert.Equal(provider, response["provider"]);
        }

        [Fact]
        public async Task GetExternalUserInfo_LogsCorrectInformation()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var provider = "google-oauth2";

            var externalUserInfo = new IExternalUserInfoService.ExternalUserInfo
            {
                UserInfoClaims = JsonDocument.Parse(@"{""sub"": ""external-user""}"),
                ExternalProvider = provider
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenAsync(accessToken))
                .ReturnsAsync(subject);

            _mockExternalUserInfoService.Setup(x => x.GetExternalUserInfoAsync(
                It.IsAny<IExternalUserInfoService.GetExternalUserInfoRequest>()))
                .ReturnsAsync(externalUserInfo);

            // Act
            var result = await _controller.GetExternalUserInfo(provider);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            // ログが正しく呼ばれたことを確認
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("External UserInfo endpoint accessed")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("External user info retrieved successfully")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}
