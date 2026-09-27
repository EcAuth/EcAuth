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
            Assert.Contains("設定が不正", ex.Message);
        }

        [Fact]
        public async Task Resolve_BothDiscounts_Throws()
        {
            await Seed(new AccountBillingPlan { AccountSubject = "acct-1", DiscountPercent = 10, DiscountJpy = 100 });

            await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync("acct-1", Month("2026-09")));
        }

        [Theory]
        [InlineData(-10, null)]
        [InlineData(101, null)]
        [InlineData(null, -500L)]
        public async Task Resolve_OutOfRangeDiscount_ThrowsInsteadOfDroppingDiscount(int? percent, long? fixedJpy)
        {
            // InMemory は CHECK 制約を強制しないので、DB を素通りした値もアプリ側で弾けることを確かめる
            await Seed(new AccountBillingPlan { AccountSubject = "acct-1", DiscountPercent = percent, DiscountJpy = fixedJpy });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync("acct-1", Month("2026-09")));
            Assert.Contains("設定が不正", ex.Message);
        }

        [Theory]
        [InlineData(-1, null)]
        [InlineData(101, null)]
        [InlineData(null, -1L)]
        [InlineData(10, 100L)]
        public async Task CheckConstraints_RejectOutOfRangeDiscountsInDatabase(int? percent, long? fixedJpy)
        {
            using var db = new RetryingSqliteContext();
            db.Context.Organizations.Add(new Organization { Id = 1, Code = "accounts", Name = "EcAuth", TenantName = "accounts" });
            db.Context.Accounts.Add(new Account { Id = 1, Subject = "acct-1", Email = "a@example.jp", OrganizationId = 1 });
            await db.Context.SaveChangesAsync();

            db.Context.AccountBillingPlans.Add(new AccountBillingPlan { AccountSubject = "acct-1", DiscountPercent = percent, DiscountJpy = fixedJpy });

            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.Context.SaveChangesAsync());
        }

        [Fact]
        public async Task CheckConstraints_AcceptBoundaryDiscounts()
        {
            using var db = new RetryingSqliteContext();
            db.Context.Organizations.Add(new Organization { Id = 1, Code = "accounts", Name = "EcAuth", TenantName = "accounts" });
            db.Context.Accounts.AddRange(
                new Account { Id = 1, Subject = "acct-1", Email = "a@example.jp", OrganizationId = 1 },
                new Account { Id = 2, Subject = "acct-2", Email = "b@example.jp", OrganizationId = 1 },
                new Account { Id = 3, Subject = "acct-3", Email = "c@example.jp", OrganizationId = 1 });
            db.Context.AccountBillingPlans.AddRange(
                new AccountBillingPlan { AccountSubject = "acct-1", DiscountPercent = 0 },
                new AccountBillingPlan { AccountSubject = "acct-2", DiscountPercent = 100 },
                new AccountBillingPlan { AccountSubject = "acct-3", DiscountJpy = 0 });

            await db.Context.SaveChangesAsync();
        }

        [Theory]
        [InlineData(0)]     // 空の期間（from == until）
        [InlineData(-31)]   // 逆転（until が from より前）
        public async Task Resolve_InvertedOrEmptyValidity_ThrowsInsteadOfFallingBackToDefault(int untilOffsetDays)
        {
            // 逆転した期間はどの月にも効かず、合意した除外が黙って消える。期間判定より先に弾く。
            var from = Month("2026-09").Start;
            await Seed(new AccountBillingPlan
            {
                AccountSubject = "acct-1",
                BillingExempt = true,
                ValidFrom = from,
                ValidUntil = from.AddDays(untilOffsetDays),
            });

            foreach (var month in new[] { "2026-07", "2026-09", "2026-11" })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync("acct-1", Month(month)));
                Assert.Contains("valid_from", ex.Message);
            }
        }

        [Fact]
        public async Task CheckConstraints_RejectInvertedValidityInDatabase()
        {
            using var db = new RetryingSqliteContext();
            db.Context.Organizations.Add(new Organization { Id = 1, Code = "accounts", Name = "EcAuth", TenantName = "accounts" });
            db.Context.Accounts.Add(new Account { Id = 1, Subject = "acct-1", Email = "a@example.jp", OrganizationId = 1 });
            await db.Context.SaveChangesAsync();
            var from = Month("2026-09").Start;

            db.Context.AccountBillingPlans.Add(new AccountBillingPlan { AccountSubject = "acct-1", ValidFrom = from, ValidUntil = from.AddDays(-1) });

            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.Context.SaveChangesAsync());
        }

        public void Dispose() => _context.Dispose();
    }
}
