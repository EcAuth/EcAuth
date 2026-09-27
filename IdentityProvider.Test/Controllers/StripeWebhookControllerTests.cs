using System.Text;
using IdentityProvider.Controllers;
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
    public class StripeWebhookControllerTests
    {
        private readonly Mock<IStripeGateway> _stripe = new();
        private readonly Mock<IBillingService> _billing = new();
        private readonly Mock<ITenantService> _tenant = new();

        private StripeWebhookController CreateController(bool enabled, string body, string? signature)
        {
            _tenant.SetupGet(t => t.TenantName).Returns("stg-accounts");
            var controller = new StripeWebhookController(
                _stripe.Object,
                _billing.Object,
                _tenant.Object,
                Options.Create(new BillingOptions { Enabled = enabled }),
                new Mock<ILogger<StripeWebhookController>>().Object);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            if (signature != null)
            {
                httpContext.Request.Headers["Stripe-Signature"] = signature;
            }
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        [Fact]
        public async Task Receive_Disabled_Returns404WithoutParsing()
        {
            var controller = CreateController(enabled: false, body: "{}", signature: "sig");

            Assert.IsType<NotFoundObjectResult>(await controller.Receive());
            _stripe.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Receive_InvalidSignature_Returns400AndDoesNotProcess()
        {
            _stripe.Setup(s => s.ParseWebhookEvent("stg-accounts", "{}", "bad"))
                .Throws(new StripeWebhookSignatureException("bad signature"));
            var controller = CreateController(enabled: true, body: "{}", signature: "bad");

            var result = await controller.Receive();

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            var error = (string)bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)!;
            Assert.Equal("invalid_signature", error);
            _billing.Verify(b => b.HandleWebhookAsync(It.IsAny<string>(), It.IsAny<StripeWebhookEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Receive_ValidEvent_PassesTenantAndReportsProcessed()
        {
            var envelope = new StripeWebhookEnvelope("evt_1", "setup_intent.succeeded", "cus_1", null);
            _stripe.Setup(s => s.ParseWebhookEvent("stg-accounts", "{\"id\":\"evt_1\"}", "sig")).Returns(envelope);
            _billing.Setup(b => b.HandleWebhookAsync("stg-accounts", envelope, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var controller = CreateController(enabled: true, body: "{\"id\":\"evt_1\"}", signature: "sig");

            var ok = Assert.IsType<OkObjectResult>(await controller.Receive());

            var processed = (bool)ok.Value!.GetType().GetProperty("processed")!.GetValue(ok.Value)!;
            Assert.True(processed);
        }

        [Fact]
        public async Task Receive_Redelivery_Returns200WithProcessedFalse()
        {
            var envelope = new StripeWebhookEnvelope("evt_1", "setup_intent.succeeded", "cus_1", null);
            _stripe.Setup(s => s.ParseWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(envelope);
            _billing.Setup(b => b.HandleWebhookAsync(It.IsAny<string>(), envelope, It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var controller = CreateController(enabled: true, body: "{}", signature: null);

            var ok = Assert.IsType<OkObjectResult>(await controller.Receive());

            var processed = (bool)ok.Value!.GetType().GetProperty("processed")!.GetValue(ok.Value)!;
            Assert.False(processed);
        }
    }
}
