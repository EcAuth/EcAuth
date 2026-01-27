using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class UserinfoControllerTests
    {
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<IUserService> _mockUserService;
        private readonly Mock<IB2BUserService> _mockB2BUserService;
        private readonly Mock<ILogger<UserinfoController>> _mockLogger;
        private readonly UserinfoController _controller;

        public UserinfoControllerTests()
        {
            _mockTokenService = new Mock<ITokenService>();
            _mockUserService = new Mock<IUserService>();
            _mockB2BUserService = new Mock<IB2BUserService>();
            _mockLogger = new Mock<ILogger<UserinfoController>>();

            _controller = new UserinfoController(
                _mockTokenService.Object,
                _mockUserService.Object,
                _mockB2BUserService.Object,
                _mockLogger.Object);

            // HttpContext のセットアップ
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Fact]
        public async Task Get_ValidAccessToken_ReturnsUserInfo()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var user = new EcAuthUser
            {
                Subject = subject,
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ReturnsAsync(user);

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal(subject, subProperty);
        }

        [Fact]
        public async Task Post_ValidAccessToken_ReturnsUserInfo()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var user = new EcAuthUser
            {
                Subject = subject,
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ReturnsAsync(user);

            // Act
            var result = await _controller.Post();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal(subject, subProperty);
        }

        [Fact]
        public async Task Get_MissingAuthorizationHeader_ReturnsUnauthorized()
        {
            // Arrange
            // Authorization ヘッダーを設定しない

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_InvalidAuthorizationHeaderFormat_ReturnsUnauthorized()
        {
            // Arrange
            _controller.HttpContext.Request.Headers["Authorization"] = "Invalid Header Format";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_NonBearerAuthenticationScheme_ReturnsUnauthorized()
        {
            // Arrange
            _controller.HttpContext.Request.Headers["Authorization"] = "Basic dGVzdDpwYXNzd29yZA==";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_EmptyAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            _controller.HttpContext.Request.Headers["Authorization"] = "Bearer ";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_InvalidAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "invalid-access-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = false,
                    Subject = null,
                    SubjectType = null
                });

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_ExpiredAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "expired-access-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = false, // 期限切れの場合はIsValid=false
                    Subject = null,
                    SubjectType = null
                });

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_UserNotFound_ReturnsUnauthorized()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "nonexistent-subject";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ReturnsAsync((EcAuthUser?)null);

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task Get_TokenServiceThrowsException_ReturnsInternalServerError()
        {
            // Arrange
            var accessToken = "valid-access-token";
            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ThrowsAsync(new Exception("Database connection failed"));

            // Act
            var result = await _controller.Get();

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);

            var response = statusCodeResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("server_error", errorProperty);
        }

        [Fact]
        public async Task Get_UserServiceThrowsException_ReturnsInternalServerError()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ThrowsAsync(new Exception("User service failed"));

            // Act
            var result = await _controller.Get();

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);

            var response = statusCodeResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("server_error", errorProperty);
        }

        [Theory]
        [InlineData("test-subject-1")]
        [InlineData("user-12345")]
        [InlineData("1234567890abcdef")]
        public async Task Get_VariousValidSubjects_ReturnsCorrectUserInfo(string subject)
        {
            // Arrange
            var accessToken = "valid-access-token";
            var user = new EcAuthUser
            {
                Subject = subject,
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ReturnsAsync(user);

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal(subject, subProperty);
        }

        [Fact]
        public async Task ProcessUserInfoRequest_LogsCorrectInformation()
        {
            // Arrange
            var accessToken = "valid-access-token";
            var subject = "test-subject";
            var user = new EcAuthUser
            {
                Subject = subject,
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            _mockTokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(accessToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = SubjectType.B2C
                });

            _mockUserService.Setup(x => x.GetUserBySubjectAsync(subject))
                .ReturnsAsync(user);

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            // ログが正しく呼ばれたことを確認
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("UserInfo endpoint accessed")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("UserInfo request processed successfully")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}