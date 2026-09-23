using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services.Billing
{
    /// <inheritdoc cref="IPricingPlanResolver" />
    public sealed class PricingPlanResolver : IPricingPlanResolver
    {
        private readonly EcAuthDbContext _context;

        public PricingPlanResolver(EcAuthDbContext context)
        {
            _context = context;
        }

        /// <inheritdoc />
        public async Task<PricingPlan> ResolveAsync(string accountSubject, UsageMonth month, CancellationToken cancellationToken = default)
        {
            var row = await _context.AccountBillingPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.AccountSubject == accountSubject, cancellationToken);
            if (row == null)
            {
                return PricingPlan.Default;
            }

            return ToPlan(row, month);
        }

        /// <summary>
        /// 行を対象月に当てはめる。有効期間外なら <see cref="PricingPlan.Default"/>。
        /// 判定は月の開始時点で行い、月の途中で切り替わる日割りはしない（<see cref="AccountBillingPlan"/> 参照）。
        /// </summary>
        public static PricingPlan ToPlan(AccountBillingPlan row, UsageMonth month)
        {
            var at = month.Start;
            if ((row.ValidFrom != null && row.ValidFrom > at) || (row.ValidUntil != null && row.ValidUntil <= at))
            {
                return PricingPlan.Default;
            }

            if (row.DiscountPercent != null && row.DiscountJpy != null)
            {
                throw new InvalidOperationException(
                    $"account_billing_plan(id={row.Id}) は割引率と定額割引を両方持っています。どちらか一方にしてください。");
            }

            try
            {
                return new PricingPlan(
                    row.BillingExempt,
                    row.ExemptReason,
                    row.DiscountPercent,
                    row.DiscountJpy,
                    row.B2BTiersJson == null ? null : PricingTable.ParseTiers(row.B2BTiersJson),
                    row.B2CTiersJson == null ? null : PricingTable.ParseTiers(row.B2CTiersJson));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"account_billing_plan(id={row.Id}) の独自料金表が不正です: {ex.Message}", ex);
            }
        }
    }
}
