using IdentityProvider.Controllers;
using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class SignupControllerTests
    {
        private readonly Mock<ISignupService> _mockSignupService;
        private readonly SignupController _controller;

        public SignupControllerTests()
        {
            _mockSignupService = new Mock<ISignupService>();
            var logger = new Mock<ILogger<SignupController>>();
            _controller = new SignupController(_mockSignupService.Object, logger.Object);
        }

        // ---- request ----

        [Fact]
        public async Task Request_ValidInput_Returns202()
        {
            _mockSignupService
                .Setup(x => x.RequestAsync(It.IsAny<SignupInput>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignupRequest { ConfirmToken = "token", Email = "owner@example.com" });

            var body = new SignupController.SignupRequestDto
            {
                Email = "owner@example.com",
                OrganizationName = "Example Shop",
                ProductionSiteUrl = "https://shop.example.jp",
                EcCubeVersion = "4"
            };

            var result = await _controller.Request(body, CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            _mockSignupService.Verify(
                x => x.RequestAsync(
                    It.Is<SignupInput>(i => i.Email == "owner@example.com" && i.EcCubeVersion == "4"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Request_ValidationException_Returns422WithErrorBody()
        {
            _mockSignupService
                .Setup(x => x.RequestAsync(It.IsAny<SignupInput>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SignupValidationException("invalid_email", "メールアドレスの形式が正しくありません。", field: "email"));

            var body = new SignupController.SignupRequestDto { Email = "bad" };

            var result = await _controller.Request(body, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(422, objectResult.StatusCode);
        }

        // ---- confirm ----

        [Fact]
        public async Task Confirm_ValidToken_Returns200()
        {
            _mockSignupService
                .Setup(x => x.ConfirmAsync("valid-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignupRequest { ConfirmToken = "valid-token", Email = "owner@example.com" });

            var body = new SignupController.SignupConfirmDto { Token = "valid-token" };

            var result = await _controller.Confirm(body, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
        }

        [Fact]
        public async Task Confirm_CodeCollision_Returns409()
        {
            _mockSignupService
                .Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SignupValidationException(
                    "organization_already_exists", "既に登録されています。", field: "production_site_url", statusCode: 409));

            var body = new SignupController.SignupConfirmDto { Token = "race-token" };

            var result = await _controller.Confirm(body, CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(409, objectResult.StatusCode);
        }

        // ---- status ----

        [Theory]
        [InlineData(SignupStatus.Pending, "pending")]
        [InlineData(SignupStatus.Confirmed, "confirmed")]
        [InlineData(SignupStatus.Expired, "expired")]
        [InlineData(SignupStatus.NotFound, "not_found")]
        public async Task Status_ReturnsStatusString(SignupStatus status, string expected)
        {
            _mockSignupService
                .Setup(x => x.GetStatusAsync("some-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(status);

            var result = await _controller.Status("some-token", CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode);
            var statusProp = ok.Value!.GetType().GetProperty("status")!.GetValue(ok.Value) as string;
            Assert.Equal(expected, statusProp);
        }
    }
}
