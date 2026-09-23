using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class BillingControllerTests
    {
        private const string AccountToken = "account-access-token";
        private const string AccountSubject = "account-subject-1";

        private readonly Mock<ITokenService> _tokenService = new();
        private readonly Mock<IBillingService> _billing = new();

        private BillingController CreateController(bool enabled, string? bearer)
        {
            var controller = new BillingController(
                _tokenService.Object,
                _billing.Object,
                Options.Create(new BillingOptions { Enabled = enabled }),
                new Mock<ILogger<BillingController>>().Object);
            var httpContext = new DefaultHttpContext();
            if (bearer != null)
            {
                httpContext.Request.Headers["Authorization"] = $"Bearer {bearer}";
            }
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private void SetupValidAccountToken()
        {
            _tokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(AccountToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = AccountSubject,
                    SubjectType = SubjectType.Account,
                });
        }

        private static IBillingService.Status EmptyStatus(UsageMonth month) =>
            new(false, null, new IBillingService.Estimate(
                month, DateTimeOffset.UtcNow, 0, 0, 0,
                new IBillingService.PlanSummary(false, null, null, false, false),
                Array.Empty<IBillingService.OrganizationEstimate>()));

        private static string ErrorOf(IActionResult result)
        {
            var obj = Assert.IsAssignableFrom<ObjectResult>(result).Value!;
            return (string)obj.GetType().GetProperty("error")!.GetValue(obj)!;
        }

        [Fact]
        public async Task GetStatus_BillingDisabled_Returns404BeforeAuth()
        {
            var controller = CreateController(enabled: false, bearer: null);

            var result = await controller.GetStatus();

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("not_found", ErrorOf(result));
            _tokenService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task GetStatus_NoBearer_Returns401()
        {
            var controller = CreateController(enabled: true, bearer: null);

            var result = await controller.GetStatus();

            Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal("invalid_token", ErrorOf(result));
        }

        [Fact]
        public async Task GetStatus_B2BToken_Returns401()
        {
            _tokenService.Setup(x => x.ValidateAccessTokenWithTypeAsync(AccountToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult { IsValid = true, Subject = "u", SubjectType = SubjectType.B2B });
            var controller = CreateController(enabled: true, bearer: AccountToken);

            Assert.IsType<UnauthorizedObjectResult>(await controller.GetStatus());
        }

        [Fact]
        public async Task GetStatus_InvalidYearMonth_Returns422()
        {
            SetupValidAccountToken();
            var controller = CreateController(enabled: true, bearer: AccountToken);

            var result = await controller.GetStatus(year_month: "2026/09");

            Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal("invalid_request", ErrorOf(result));
        }

        [Fact]
        public async Task GetStatus_FutureMonth_Returns422()
        {
            SetupValidAccountToken();
            var controller = CreateController(enabled: true, bearer: AccountToken);
            var future = UsageMonth.FromInstant(DateTimeOffset.UtcNow.AddMonths(2));

            Assert.IsType<UnprocessableEntityObjectResult>(await controller.GetStatus(year_month: future.Value));
        }

        [Fact]
        public async Task GetStatus_DefaultsToCurrentMonth_AndPassesRefreshFlag()
        {
            SetupValidAccountToken();
            var current = UsageMonth.Current();
            _billing.Setup(b => b.GetStatusAsync(AccountSubject, current, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EmptyStatus(current));
            var controller = CreateController(enabled: true, bearer: AccountToken);

            var result = await controller.GetStatus(refresh: "1");

            Assert.IsType<OkObjectResult>(result);
            _billing.Verify(b => b.GetStatusAsync(AccountSubject, current, true, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetStatus_AccountMissing_Returns401()
        {
            SetupValidAccountToken();
            _billing.Setup(b => b.GetStatusAsync(AccountSubject, It.IsAny<UsageMonth>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IBillingService.Status?)null);
            var controller = CreateController(enabled: true, bearer: AccountToken);

            Assert.IsType<UnauthorizedObjectResult>(await controller.GetStatus());
        }

        [Fact]
        public async Task Checkout_Disabled_Returns404()
        {
            var controller = CreateController(enabled: false, bearer: AccountToken);
            Assert.IsType<NotFoundObjectResult>(await controller.CreateCheckoutSession());
        }

        [Fact]
        public async Task Checkout_ReturnsUrl()
        {
            SetupValidAccountToken();
            _billing.Setup(b => b.CreateCheckoutSessionAsync(AccountSubject, It.IsAny<CancellationToken>()))
                .ReturnsAsync("https://checkout.stripe.com/c/pay/cs_test_1");
            var controller = CreateController(enabled: true, bearer: AccountToken);

            var ok = Assert.IsType<OkObjectResult>(await controller.CreateCheckoutSession());
            var url = (string)ok.Value!.GetType().GetProperty("url")!.GetValue(ok.Value)!;
            Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_1", url);
        }

        [Fact]
        public async Task Portal_NoCustomer_Returns409()
        {
            SetupValidAccountToken();
            _billing.Setup(b => b.CreatePortalSessionAsync(AccountSubject, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new BillingException(StatusCodes.Status409Conflict, "no_customer", "支払い方法が未登録です。"));
            var controller = CreateController(enabled: true, bearer: AccountToken);

            var result = await controller.CreatePortalSession();

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(409, obj.StatusCode);
            Assert.Equal("no_customer", ErrorOf(result));
        }

        [Fact]
        public async Task Portal_NoBearer_Returns401()
        {
            var controller = CreateController(enabled: true, bearer: null);
            Assert.IsType<UnauthorizedObjectResult>(await controller.CreatePortalSession());
        }
    }
}
