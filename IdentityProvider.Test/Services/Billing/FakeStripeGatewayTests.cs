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
        public void ParseWebhookEvent_InvoiceObject_TakesInvoiceId()
        {
            var envelope = _gateway.ParseWebhookEvent("accounts",
                """{"id":"evt_3","type":"invoice.paid","data":{"object":{"object":"invoice","id":"in_3","customer":"cus_3"}}}""",
                null);

            Assert.Equal(new StripeWebhookEnvelope("evt_3", "invoice.paid", "cus_3", null, "in_3"), envelope);
        }

        [Fact]
        public async Task Invoice_IdempotencyKeyReturnsSameInvoiceAndItem_AndCancelFollowsStatus()
        {
            var request = new InvoiceDraftRequest("cus_1", "test", new Dictionary<string, string>());
            var first = await _gateway.CreateInvoiceAsync("accounts", request, "key-1", CancellationToken.None);
            var again = await _gateway.CreateInvoiceAsync("accounts", request, "key-1", CancellationToken.None);
            Assert.Equal(first.Id, again.Id);

            var item = await _gateway.AddInvoiceItemAsync("accounts", "cus_1", first.Id, 1_234, "x", new Dictionary<string, string>(), "item-1", CancellationToken.None);
            Assert.Equal(item, await _gateway.AddInvoiceItemAsync("accounts", "cus_1", first.Id, 1_234, "x", new Dictionary<string, string>(), "item-1", CancellationToken.None));

            var snapshot = await _gateway.GetInvoiceAsync("accounts", first.Id, CancellationToken.None);
            Assert.Equal(123, snapshot!.TaxJpy);   // 1,234 × 10% = 123.4 → 請求書単位で 1 回だけ四捨五入
            Assert.Equal(1_357, snapshot.TotalJpy);

            _gateway.SetInvoiceStatus(first.Id, "paid");
            await Assert.ThrowsAsync<InvalidOperationException>(() => _gateway.CancelInvoiceAsync("accounts", first.Id, CancellationToken.None));
            _gateway.SetInvoiceStatus(first.Id, "draft");
            Assert.Equal(StripeInvoiceSnapshot.DeletedStatus, (await _gateway.CancelInvoiceAsync("accounts", first.Id, CancellationToken.None)).Status);
            Assert.Null(await _gateway.GetInvoiceAsync("accounts", first.Id, CancellationToken.None));
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
            Assert.Equal(PaymentMethodState.NotRegistered, await _gateway.EnsureDefaultPaymentMethodAsync("accounts", customer, default));

            var url = await _gateway.CreateSetupCheckoutSessionAsync("accounts", customer, "https://x/ok", "https://x/ng", default);

            Assert.Equal("https://x/ok", url);
            Assert.Equal(PaymentMethodState.Registered, await _gateway.EnsureDefaultPaymentMethodAsync("accounts", customer, default));
        }

        [Fact]
        public async Task DeletedOrUnknownCustomer_IsCustomerDeleted()
        {
            var customer = await _gateway.CreateCustomerAsync("accounts", "a@example.jp", null, new Dictionary<string, string>(), "k1", default);
            _gateway.DeleteCustomer(customer);

            Assert.Equal(PaymentMethodState.CustomerDeleted, await _gateway.EnsureDefaultPaymentMethodAsync("accounts", customer, default));
            Assert.Equal(PaymentMethodState.CustomerDeleted, await _gateway.EnsureDefaultPaymentMethodAsync("accounts", "cus_unknown", default));
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
