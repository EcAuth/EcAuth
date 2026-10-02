using System.Security.Cryptography;
using System.Text;
using IdentityProvider.Services.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Stripe;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    /// <summary>
    /// <see cref="StripeGateway.ToEnvelope"/>: 署名検証後の <see cref="Event"/> から Customer を取り出す部分。
    /// 本物の Stripe には接続しない（署名検証そのものは Stripe.net の責務）。
    /// </summary>
    public class StripeGatewayTests
    {
        private static Event MakeEvent(string type, IHasObject obj, string? previousAttributesJson = null) => new()
        {
            Id = "evt_1",
            Type = type,
            Data = new EventData
            {
                Object = obj,
                PreviousAttributes = previousAttributesJson == null ? null : JObject.Parse(previousAttributesJson),
            },
        };

        [Fact]
        public void ToEnvelope_CheckoutSession_TakesCustomerAndMode()
        {
            var envelope = StripeGateway.ToEnvelope(MakeEvent(
                "checkout.session.completed",
                new Stripe.Checkout.Session { CustomerId = "cus_1", Mode = "setup" }));

            Assert.Equal(new StripeWebhookEnvelope("evt_1", "checkout.session.completed", "cus_1", "setup"), envelope);
        }

        [Fact]
        public void ToEnvelope_PaymentMethodDetached_FallsBackToPreviousAttributesCustomer()
        {
            // デタッチ後の PaymentMethod は customer が null。previous_attributes に変更前の Customer が載る。
            var envelope = StripeGateway.ToEnvelope(MakeEvent(
                "payment_method.detached",
                new PaymentMethod { Id = "pm_1", CustomerId = null },
                """{"customer":"cus_prev"}"""));

            Assert.Equal("cus_prev", envelope.CustomerId);
            Assert.Null(envelope.CheckoutMode);
        }

        [Fact]
        public void ToEnvelope_PaymentMethodAttached_UsesObjectCustomer()
        {
            var envelope = StripeGateway.ToEnvelope(MakeEvent(
                "payment_method.attached",
                new PaymentMethod { Id = "pm_1", CustomerId = "cus_now" },
                """{"customer":"cus_prev"}"""));

            Assert.Equal("cus_now", envelope.CustomerId);
        }

        [Fact]
        public void ToEnvelope_NoCustomerAnywhere_IsNull()
        {
            var envelope = StripeGateway.ToEnvelope(MakeEvent(
                "payment_method.detached",
                new PaymentMethod { Id = "pm_1", CustomerId = null },
                """{"metadata":{}}"""));

            Assert.Null(envelope.CustomerId);
        }

        [Fact]
        public void ToEnvelope_CustomerObject_UsesItsId()
        {
            var envelope = StripeGateway.ToEnvelope(MakeEvent("customer.updated", new Customer { Id = "cus_2" }));
            Assert.Equal("cus_2", envelope.CustomerId);
        }

        // ---- ParseWebhookEvent（署名検証は Stripe.net。ここでは例外の種類を 400 に揃えることを確かめる） ----

        private const string WebhookSecret = "whsec_test_secret";
        private const string WebhookPayload = """{"id":"evt_1","object":"event","type":"customer.updated","data":{"object":{"id":"cus_1","object":"customer"}}}""";

        private static StripeGateway MakeGateway() => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret:accounts"] = WebhookSecret })
                .Build(),
            NullLogger<StripeGateway>.Instance);

        private static string Sign(string payload, long timestamp)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
            var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
            return $"t={timestamp},v1={Convert.ToHexString(signature).ToLowerInvariant()}";
        }

        [Fact]
        public void ParseWebhookEvent_ValidSignature_ReturnsEnvelope()
        {
            var header = Sign(WebhookPayload, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var envelope = MakeGateway().ParseWebhookEvent("accounts", WebhookPayload, header);

            Assert.Equal("evt_1", envelope.Id);
            Assert.Equal("cus_1", envelope.CustomerId);
        }

        [Theory]
        [InlineData(null)]       // ヘッダー無し。Stripe.net は NullReferenceException を投げる（本番で 500 になった）
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]  // t= / v1= の無い形
        [InlineData("t=1,v1=deadbeef")]
        public void ParseWebhookEvent_MissingOrInvalidSignature_ThrowsSignatureException(string? header)
        {
            Assert.Throws<StripeWebhookSignatureException>(
                () => MakeGateway().ParseWebhookEvent("accounts", WebhookPayload, header));
        }

        [Fact]
        public void ConfigKey_NormalizesTenantName()
        {
            Assert.Equal("Stripe:SecretKey:stg_accounts", StripeGateway.ConfigKey("SecretKey", "stg-accounts"));
            Assert.Equal("Stripe:WebhookSecret:accounts", StripeGateway.ConfigKey("WebhookSecret", "accounts"));
        }
    }
}
