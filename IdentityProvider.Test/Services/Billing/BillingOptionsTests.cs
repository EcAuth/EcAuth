using IdentityProvider.Services.Billing;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    public class BillingOptionsTests
    {
        [Theory]
        [InlineData(null, BillingOptions.StripeProvider)]
        [InlineData("", BillingOptions.StripeProvider)]
        [InlineData("Stripe", BillingOptions.StripeProvider)]
        [InlineData("stripe", BillingOptions.StripeProvider)]
        [InlineData("Fake", BillingOptions.FakeProvider)]
        [InlineData("FAKE", BillingOptions.FakeProvider)]
        public void ResolveProvider_KnownValues(string? configured, string expected)
        {
            Assert.Equal(expected, BillingOptions.ResolveProvider(configured));
        }

        [Theory]
        [InlineData("Fkae")]
        [InlineData("Stripe ")]
        [InlineData("mock")]
        public void ResolveProvider_UnknownValue_FailsInsteadOfFallingBackToStripe(string configured)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => BillingOptions.ResolveProvider(configured));
            Assert.Contains("Billing:Provider", ex.Message);
        }
    }
}
