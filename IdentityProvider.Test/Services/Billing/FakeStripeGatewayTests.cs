using IdentityProvider.Services.Billing;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    /// <summary>
    /// <see cref="FakeStripeGateway"/> の Webhook パーサ。E2E が直接 POST する JSON を読む経路で、
    /// 壊れたボディは必ず <see cref="StripeWebhookSignatureException"/>（→ 400）にする（500 にしない）。
    /// </summary>
    public class FakeStripeGatewayTests
    {
        private readonly FakeStripeGateway _gateway = new();

        [Fact]
        public void ParseWebhookEvent_ReadsIdTypeCustomerAndMode()
        {
            var envelope = _gateway.ParseWebhookEvent("accounts",
                """{"id":"evt_1","type":"checkout.session.completed","data":{"object":{"object":"checkout.session","customer":"cus_1","mode":"setup"}}}""",
                null);

            Assert.Equal(new StripeWebhookEnvelope("evt_1", "checkout.session.completed", "cus_1", "setup"), envelope);
        }

        [Fact]
        public void ParseWebhookEvent_CustomerObject_UsesItsId()
        {
            var envelope = _gateway.ParseWebhookEvent("accounts",
                """{"id":"evt_2","type":"customer.updated","data":{"object":{"object":"customer","id":"cus_2"}}}""",
                null);

            Assert.Equal("cus_2", envelope.CustomerId);
            Assert.Null(envelope.CheckoutMode);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("\"not json\"")]          // JSON 文字列（Playwright が文字列 data をこう送る）
        [InlineData("[1,2]")]
        [InlineData("{}")]
        [InlineData("""{"id":1,"type":"x"}""")]
        [InlineData("""{"id":"evt","type":null}""")]
        public void ParseWebhookEvent_BrokenBody_ThrowsSignatureException(string payload)
        {
            Assert.Throws<StripeWebhookSignatureException>(() => _gateway.ParseWebhookEvent("accounts", payload, null));
        }

        [Fact]
        public async Task Checkout_MarksCustomerAsHavingPaymentMethod()
        {
            var customer = await _gateway.CreateCustomerAsync("accounts", "a@example.jp", null, new Dictionary<string, string>(), "k1", default);
            Assert.False(await _gateway.EnsureDefaultPaymentMethodAsync("accounts", customer, default));

            var url = await _gateway.CreateSetupCheckoutSessionAsync("accounts", customer, "https://x/ok", "https://x/ng", default);

            Assert.Equal("https://x/ok", url);
            Assert.True(await _gateway.EnsureDefaultPaymentMethodAsync("accounts", customer, default));
        }

        [Fact]
        public async Task CreateCustomer_SameIdempotencyKey_SameId()
        {
            var a = await _gateway.CreateCustomerAsync("accounts", "a@example.jp", null, new Dictionary<string, string>(), "k1", default);
            var b = await _gateway.CreateCustomerAsync("accounts", "a@example.jp", null, new Dictionary<string, string>(), "k1", default);
            var c = await _gateway.CreateCustomerAsync("accounts", "a@example.jp", null, new Dictionary<string, string>(), "k2", default);

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }
}
