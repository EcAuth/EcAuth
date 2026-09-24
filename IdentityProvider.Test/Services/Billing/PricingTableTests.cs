using IdentityProvider.Models;
using IdentityProvider.Services.Billing;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    public class PricingTableTests
    {
        [Fact]
        public void StandardTables_AreValidAndMatchRequirements()
        {
            PricingTable.Validate(PricingTable.B2B);
            PricingTable.Validate(PricingTable.B2C);
            Assert.Equal(5, PricingTable.FreeTierMau(SubjectType.B2B));
            Assert.Equal(50, PricingTable.FreeTierMau(SubjectType.B2C));
        }

        [Fact]
        public void For_Account_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PricingTable.For(SubjectType.Account));
        }

        [Fact]
        public void ParseTiers_RoundTripsThroughSerializeTiers()
        {
            var tiers = new[] { new PriceTier(5, 0), new PriceTier(1_000, 80), new PriceTier(null, 60) };

            var json = PricingTable.SerializeTiers(tiers);
            var parsed = PricingTable.ParseTiers(json);

            Assert.Equal("""[{"up_to":5,"unit_price_jpy":0},{"up_to":1000,"unit_price_jpy":80},{"up_to":null,"unit_price_jpy":60}]""", json);
            Assert.Equal(tiers, parsed);
        }

        [Theory]
        [InlineData("[]", "1 帯以上")]
        [InlineData("""[{"up_to":5,"unit_price_jpy":0}]""", "最後の帯の up_to は null")]
        [InlineData("""[{"up_to":null,"unit_price_jpy":0},{"up_to":null,"unit_price_jpy":10}]""", "null は最後の帯だけ")]
        [InlineData("""[{"up_to":10,"unit_price_jpy":0},{"up_to":10,"unit_price_jpy":5},{"up_to":null,"unit_price_jpy":1}]""", "より大きく")]
        [InlineData("""[{"up_to":10,"unit_price_jpy":-1},{"up_to":null,"unit_price_jpy":1}]""", "単価が負")]
        [InlineData("not json", "JSON を読めません")]
        [InlineData("null", "null")]
        public void ParseTiers_RejectsMalformedTables(string json, string expectedMessagePart)
        {
            var ex = Assert.Throws<ArgumentException>(() => PricingTable.ParseTiers(json));
            Assert.Contains(expectedMessagePart, ex.Message);
        }

        [Fact]
        public void FreeTierMau_AllFreeTable_IsUnbounded()
        {
            Assert.Equal(int.MaxValue, PricingTable.FreeTierMau(new[] { new PriceTier(null, 0) }));
        }
    }
}
