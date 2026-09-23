using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using IdentityProvider.Test.TestHelpers;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    public class PricingPlanResolverTests : IDisposable
    {
        private readonly EcAuthDbContext _context = TestDbContextHelper.CreateInMemoryContext();
        private readonly PricingPlanResolver _resolver;

        public PricingPlanResolverTests()
        {
            _resolver = new PricingPlanResolver(_context);
        }

        private static UsageMonth Month(string value)
        {
            Assert.True(UsageMonth.TryParse(value, out var month));
            return month;
        }

        private async Task Seed(AccountBillingPlan plan)
        {
            _context.AccountBillingPlans.Add(plan);
            await _context.SaveChangesAsync();
        }

        [Fact]
        public async Task Resolve_NoRow_ReturnsDefault()
        {
            var plan = await _resolver.ResolveAsync("acct-1", Month("2026-09"));
            Assert.Same(PricingPlan.Default, plan);
        }

        [Fact]
        public async Task Resolve_RowWithoutValidity_AppliesToAnyMonth()
        {
            await Seed(new AccountBillingPlan
            {
                AccountSubject = "acct-1",
                BillingExempt = true,
                ExemptReason = "社内利用",
                DiscountPercent = 10,
                B2CTiersJson = """[{"up_to":100,"unit_price_jpy":0},{"up_to":null,"unit_price_jpy":10}]""",
            });

            var plan = await _resolver.ResolveAsync("acct-1", Month("2020-01"));

            Assert.True(plan.Exempt);
            Assert.Equal("社内利用", plan.ExemptReason);
            Assert.Equal(10, plan.DiscountPercent);
            Assert.Null(plan.DiscountJpy);
            Assert.Null(plan.B2BTiers);
            Assert.Equal(new[] { new PriceTier(100, 0), new PriceTier(null, 10) }, plan.B2CTiers);
            Assert.Equal(100, plan.FreeTierMau(SubjectType.B2C));
            Assert.Equal(5, plan.FreeTierMau(SubjectType.B2B));
        }

        [Fact]
        public async Task Resolve_Validity_IsJudgedAtMonthStart()
        {
            // 2026-09-01 00:00 JST から 2026-11-01 00:00 JST まで（11 月は含まない）
            await Seed(new AccountBillingPlan
            {
                AccountSubject = "acct-1",
                DiscountJpy = 500,
                ValidFrom = Month("2026-09").Start,
                ValidUntil = Month("2026-11").Start,
            });

            Assert.Same(PricingPlan.Default, await _resolver.ResolveAsync("acct-1", Month("2026-08")));
            Assert.Equal(500, (await _resolver.ResolveAsync("acct-1", Month("2026-09"))).DiscountJpy);
            Assert.Equal(500, (await _resolver.ResolveAsync("acct-1", Month("2026-10"))).DiscountJpy);
            Assert.Same(PricingPlan.Default, await _resolver.ResolveAsync("acct-1", Month("2026-11")));
        }

        [Fact]
        public async Task Resolve_ValidFromInsideMonth_DoesNotApplyToThatMonth()
        {
            // 月の途中から有効 → その月の開始時点では無効なので翌月から
            await Seed(new AccountBillingPlan
            {
                AccountSubject = "acct-1",
                DiscountPercent = 50,
                ValidFrom = Month("2026-09").Start.AddDays(10),
            });

            Assert.Same(PricingPlan.Default, await _resolver.ResolveAsync("acct-1", Month("2026-09")));
            Assert.Equal(50, (await _resolver.ResolveAsync("acct-1", Month("2026-10"))).DiscountPercent);
        }

        [Fact]
        public async Task Resolve_OtherAccountRow_IsNotApplied()
        {
            await Seed(new AccountBillingPlan { AccountSubject = "acct-2", BillingExempt = true });

            Assert.Same(PricingPlan.Default, await _resolver.ResolveAsync("acct-1", Month("2026-09")));
        }

        [Fact]
        public async Task Resolve_BrokenCustomTiers_ThrowsInsteadOfFallingBack()
        {
            await Seed(new AccountBillingPlan
            {
                AccountSubject = "acct-1",
                B2BTiersJson = """[{"up_to":5,"unit_price_jpy":0}]""",
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync("acct-1", Month("2026-09")));
            Assert.Contains("独自料金表が不正", ex.Message);
        }

        [Fact]
        public async Task Resolve_BothDiscounts_Throws()
        {
            await Seed(new AccountBillingPlan { AccountSubject = "acct-1", DiscountPercent = 10, DiscountJpy = 100 });

            await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync("acct-1", Month("2026-09")));
        }

        public void Dispose() => _context.Dispose();
    }
}
