using IdentityProvider.Models;
using IdentityProvider.Services.Billing;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    /// <summary>
    /// 料金表（requirements.html §5.1 / EcAuthDocs#119）の段階従量計算。期待値は Issue 本文の月額例と帯の境界値。
    /// </summary>
    public class PricingCalculatorTests
    {
        // 標準料金表の Calculate(SubjectType, int) はインターフェースの既定実装なので、インターフェース型で持つ。
        private readonly IPricingCalculator _calculator = new PricingCalculator();

        [Theory]
        // B2C: 〜50 無料 / 51〜1,000 ¥20 / 1,001〜5,000 ¥15 / 5,001〜 ¥10
        [InlineData(0, 0, 0)]
        [InlineData(50, 0, 0)]
        [InlineData(51, 20, 1)]
        [InlineData(100, 1_000, 50)]
        [InlineData(500, 9_000, 450)]
        [InlineData(1_000, 19_000, 950)]
        [InlineData(1_001, 19_015, 951)]
        [InlineData(5_000, 79_000, 4_950)]
        [InlineData(5_001, 79_010, 4_951)]
        [InlineData(10_000, 129_000, 9_950)]
        public void Calculate_B2C_MatchesIssueExamples(int mau, long expectedJpy, int expectedBillable)
        {
            var quote = _calculator.Calculate(SubjectType.B2C, mau);

            Assert.Equal(expectedJpy, quote.AmountJpy);
            Assert.Equal(expectedBillable, quote.BillableUnits);
            Assert.Equal(50, quote.FreeTierMau);
            Assert.Equal(mau, quote.MonthlyActiveUsers);
            Assert.False(quote.CustomPricing);
        }

        [Theory]
        // B2B: 〜5 無料 / 6〜 ¥100
        [InlineData(0, 0, 0)]
        [InlineData(5, 0, 0)]
        [InlineData(6, 100, 1)]
        [InlineData(10, 500, 5)]
        [InlineData(100, 9_500, 95)]
        public void Calculate_B2B_GraduatedFromSixth(int mau, long expectedJpy, int expectedBillable)
        {
            var quote = _calculator.Calculate(SubjectType.B2B, mau);

            Assert.Equal(expectedJpy, quote.AmountJpy);
            Assert.Equal(expectedBillable, quote.BillableUnits);
            Assert.Equal(5, quote.FreeTierMau);
        }

        [Fact]
        public void Calculate_B2C_BandsCoverEveryMauExactlyOnce()
        {
            var quote = _calculator.Calculate(SubjectType.B2C, 6_000);

            Assert.Collection(quote.Bands,
                b => { Assert.Equal((1, 50, 50, 0, 0L), (b.From, b.To, b.Units, b.UnitPriceJpy, b.AmountJpy)); },
                b => { Assert.Equal((51, 1_000, 950, 20, 19_000L), (b.From, b.To, b.Units, b.UnitPriceJpy, b.AmountJpy)); },
                b => { Assert.Equal((1_001, 5_000, 4_000, 15, 60_000L), (b.From, b.To, b.Units, b.UnitPriceJpy, b.AmountJpy)); },
                b => { Assert.Equal((5_001, 6_000, 1_000, 10, 10_000L), (b.From, b.To, b.Units, b.UnitPriceJpy, b.AmountJpy)); });
            Assert.Equal(6_000, quote.Bands.Sum(b => b.Units));
            Assert.Equal(quote.AmountJpy, quote.Bands.Sum(b => b.AmountJpy));
        }

        [Fact]
        public void Calculate_ZeroMau_HasNoBands()
        {
            var quote = _calculator.Calculate(SubjectType.B2C, 0);
            Assert.Empty(quote.Bands);
        }

        [Fact]
        public void Calculate_NegativeMau_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _calculator.Calculate(SubjectType.B2C, -1));
        }

        [Fact]
        public void Calculate_AccountSubjectType_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _calculator.Calculate(SubjectType.Account, 10));
        }

        [Fact]
        public void Calculate_CustomTiers_OverrideStandardTableForThatTypeOnly()
        {
            // B2B だけ独自: 〜100 無料 / 101〜 ¥50
            var plan = PricingPlan.Default with
            {
                B2BTiers = new[] { new PriceTier(100, 0), new PriceTier(null, 50) },
            };

            var b2b = _calculator.Calculate(plan, SubjectType.B2B, 120);
            Assert.Equal(1_000, b2b.AmountJpy);
            Assert.Equal(100, b2b.FreeTierMau);
            Assert.Equal(20, b2b.BillableUnits);
            Assert.True(b2b.CustomPricing);

            var b2c = _calculator.Calculate(plan, SubjectType.B2C, 100);
            Assert.Equal(1_000, b2c.AmountJpy);
            Assert.False(b2c.CustomPricing);
        }

        [Fact]
        public void Calculate_CustomTiersWithoutFreeBand_FreeTierIsZero()
        {
            var plan = PricingPlan.Default with { B2CTiers = new[] { new PriceTier(null, 30) } };

            var quote = _calculator.Calculate(plan, SubjectType.B2C, 10);

            Assert.Equal(0, quote.FreeTierMau);
            Assert.Equal(10, quote.BillableUnits);
            Assert.Equal(300, quote.AmountJpy);
        }

        [Theory]
        [InlineData(10_000, 10, 1_000)]
        [InlineData(999, 10, 99)]      // 切り捨て（99.9 → 99）
        [InlineData(10_000, 100, 10_000)]
        [InlineData(0, 50, 0)]
        public void CalculateDiscount_Percent_FloorsToYen(long subtotal, int percent, long expected)
        {
            var plan = PricingPlan.Default with { DiscountPercent = percent };
            Assert.Equal(expected, _calculator.CalculateDiscount(plan, subtotal));
        }

        [Theory]
        [InlineData(10_000, 3_000, 3_000)]
        [InlineData(2_000, 3_000, 2_000)]  // 合計を超える分は切り捨て（請求額を負にしない）
        [InlineData(0, 3_000, 0)]
        public void CalculateDiscount_Fixed_CappedAtSubtotal(long subtotal, long discountJpy, long expected)
        {
            var plan = PricingPlan.Default with { DiscountJpy = discountJpy };
            Assert.Equal(expected, _calculator.CalculateDiscount(plan, subtotal));
        }

        [Fact]
        public void CalculateDiscount_NoDiscount_ReturnsZero()
        {
            Assert.Equal(0, _calculator.CalculateDiscount(PricingPlan.Default, 10_000));
        }

        [Fact]
        public void CalculateDiscount_BothPercentAndFixed_Throws()
        {
            var plan = PricingPlan.Default with { DiscountPercent = 10, DiscountJpy = 100 };
            Assert.Throws<ArgumentException>(() => _calculator.CalculateDiscount(plan, 10_000));
        }

        [Fact]
        public void CalculateDiscount_PercentOver100_Throws()
        {
            var plan = PricingPlan.Default with { DiscountPercent = 101 };
            Assert.Throws<ArgumentOutOfRangeException>(() => _calculator.CalculateDiscount(plan, 10_000));
        }
    }
}
