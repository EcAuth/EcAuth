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
            // 期間が逆転（または空）していると、どの月も期間外と判定されて合意した除外・割引・独自料金が黙って消える。
            // 期間判定より先に弾く（DB の CHECK 制約 CK_account_billing_plan_validity と同じ条件）。
            if (row.ValidFrom != null && row.ValidUntil != null && row.ValidFrom >= row.ValidUntil)
            {
                throw new InvalidOperationException(
                    $"account_billing_plan(id={row.Id}) の設定が不正です: valid_from（{row.ValidFrom:O}）は valid_until（{row.ValidUntil:O}）より前にしてください。");
            }

            var at = month.Start;
            if ((row.ValidFrom != null && row.ValidFrom > at) || (row.ValidUntil != null && row.ValidUntil <= at))
            {
                return PricingPlan.Default;
            }

            try
            {
                var plan = new PricingPlan(
                    row.BillingExempt,
                    row.ExemptReason,
                    row.DiscountPercent,
                    row.DiscountJpy,
                    row.B2BTiersJson == null ? null : PricingTable.ParseTiers(row.B2BTiersJson),
                    row.B2CTiersJson == null ? null : PricingTable.ParseTiers(row.B2CTiersJson));
                plan.ValidateDiscount();
                return plan;
            }
            catch (ArgumentException ex)
            {
                // 不正な設定で黙って標準料金・割引なしに落とさない（過少 / 過大請求になる）。見込み額も請求も止める。
                throw new InvalidOperationException(
                    $"account_billing_plan(id={row.Id}) の設定が不正です: {ex.Message}", ex);
            }
        }
    }
}
