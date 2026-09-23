using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <inheritdoc cref="IPricingCalculator" />
    public sealed class PricingCalculator : IPricingCalculator
    {
        /// <inheritdoc />
        public IPricingCalculator.Quote Calculate(PricingPlan plan, SubjectType subjectType, int monthlyActiveUsers)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (monthlyActiveUsers < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monthlyActiveUsers), monthlyActiveUsers, "MAU は 0 以上です。");
            }

            var tiers = plan.TiersFor(subjectType);
            var bands = new List<IPricingCalculator.Band>();
            long total = 0;
            var billable = 0;

            // 帯の下限（この帯で数える最初の MAU、1 始まり）。前の帯の上限 + 1。
            var from = 1;
            foreach (var tier in tiers)
            {
                if (from > monthlyActiveUsers)
                {
                    break;
                }

                // この帯で数える最後の MAU。上限なし（null）なら入力そのまま。
                var to = tier.UpTo is int upTo ? Math.Min(upTo, monthlyActiveUsers) : monthlyActiveUsers;
                var units = to - from + 1;
                var amount = (long)units * tier.UnitPriceJpy;

                bands.Add(new IPricingCalculator.Band(from, to, units, tier.UnitPriceJpy, amount));
                total += amount;
                if (tier.UnitPriceJpy > 0)
                {
                    billable += units;
                }

                from = to + 1;
            }

            return new IPricingCalculator.Quote(
                subjectType,
                monthlyActiveUsers,
                PricingTable.FreeTierMau(tiers),
                billable,
                total,
                bands,
                plan.HasCustomTiers(subjectType));
        }

        /// <inheritdoc />
        public long CalculateDiscount(PricingPlan plan, long subtotalJpy)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (subtotalJpy < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(subtotalJpy), subtotalJpy, "小計は 0 以上です。");
            }
            if (plan.DiscountPercent != null && plan.DiscountJpy != null)
            {
                throw new ArgumentException("割引率と定額割引は併用できません。", nameof(plan));
            }

            if (plan.DiscountPercent is int percent && percent > 0)
            {
                if (percent > 100)
                {
                    throw new ArgumentOutOfRangeException(nameof(plan), percent, "割引率は 0〜100 です。");
                }
                // 切り捨て（¥1 未満は顧客側に寄せない）。100% なら小計そのまま。
                return subtotalJpy * percent / 100;
            }

            if (plan.DiscountJpy is long fixedJpy && fixedJpy > 0)
            {
                return Math.Min(fixedJpy, subtotalJpy);
            }

            return 0;
        }
    }
}
