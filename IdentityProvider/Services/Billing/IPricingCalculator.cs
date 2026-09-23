using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// MAU から月額を計算する（EcAuthDocs#119）。料金表は <see cref="PricingTable"/>、Account 別の調整は <see cref="PricingPlan"/>。
    /// マイページの見込み額（<c>GET /v1/account/billing</c>）と月次の Invoice 起票が同じ実装を通ることで、
    /// 顧客に見せた金額と請求額が構造的に一致する。帯の計算と割引の丸めはここ以外に書かないこと。
    /// </summary>
    public interface IPricingCalculator
    {
        /// <param name="From">この帯で数えた最初の MAU（1 始まり）</param>
        /// <param name="To">この帯で数えた最後の MAU</param>
        /// <param name="Units">この帯に属する MAU 数（<c>To - From + 1</c>）</param>
        /// <param name="UnitPriceJpy">単価（円）</param>
        /// <param name="AmountJpy"><c>Units × UnitPriceJpy</c></param>
        public sealed record Band(int From, int To, int Units, int UnitPriceJpy, long AmountJpy);

        /// <param name="SubjectType">B2B / B2C</param>
        /// <param name="MonthlyActiveUsers">入力の MAU</param>
        /// <param name="FreeTierMau">無料枠（適用した料金表の先頭無料帯の上限）</param>
        /// <param name="BillableUnits">無料枠を超えた MAU 数</param>
        /// <param name="AmountJpy">合計（円、税込）。割引は含まない（割引は Account 合計に対して 1 回）</param>
        /// <param name="Bands">帯ごとの内訳（MAU 0 の帯は含めない）。請求書の明細とマイページの内訳表示に使う</param>
        /// <param name="CustomPricing">標準ではなく Account の独自料金表で計算したか</param>
        public sealed record Quote(
            SubjectType SubjectType,
            int MonthlyActiveUsers,
            int FreeTierMau,
            int BillableUnits,
            long AmountJpy,
            IReadOnlyList<Band> Bands,
            bool CustomPricing);

        /// <summary>
        /// 段階従量で 1 Client の月額を計算する。<paramref name="monthlyActiveUsers"/> が負なら <see cref="ArgumentOutOfRangeException"/>、
        /// <paramref name="subjectType"/> が課金対象外（<see cref="SubjectType.Account"/>）でも同じ。
        /// </summary>
        Quote Calculate(PricingPlan plan, SubjectType subjectType, int monthlyActiveUsers);

        /// <summary>標準料金表で計算する（<see cref="PricingPlan.Default"/>）。</summary>
        Quote Calculate(SubjectType subjectType, int monthlyActiveUsers) => Calculate(PricingPlan.Default, subjectType, monthlyActiveUsers);

        /// <summary>
        /// Account 合計（Client の <see cref="Quote.AmountJpy"/> の和）に対する割引額（正の値、円）。
        /// 率は切り捨て（顧客有利にしない側。¥1 単位）、定額は合計を上限にする（請求額を負にしない）。
        /// 割引が無ければ 0。請求書には負の 1 行として載せる。
        /// </summary>
        long CalculateDiscount(PricingPlan plan, long subtotalJpy);
    }
}
