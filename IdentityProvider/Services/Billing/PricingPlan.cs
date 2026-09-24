using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// ある Account・ある月に適用する課金条件を解決した結果（<see cref="AccountBillingPlan"/> を読んだもの）。
    /// 何も設定が無ければ <see cref="Default"/>（標準料金表、割引なし、課金する）。
    /// </summary>
    /// <param name="Exempt">Account 全体を請求しない</param>
    /// <param name="ExemptReason">その理由（運用メモ）</param>
    /// <param name="DiscountPercent">Account 合計への割引率（0〜100）。<paramref name="DiscountJpy"/> と併用不可</param>
    /// <param name="DiscountJpy">Account 合計から引く定額（円）</param>
    /// <param name="B2BTiers">B2B の独自料金表。null なら <see cref="PricingTable.B2B"/></param>
    /// <param name="B2CTiers">B2C の独自料金表。null なら <see cref="PricingTable.B2C"/></param>
    public sealed record PricingPlan(
        bool Exempt,
        string? ExemptReason,
        int? DiscountPercent,
        long? DiscountJpy,
        IReadOnlyList<PriceTier>? B2BTiers,
        IReadOnlyList<PriceTier>? B2CTiers)
    {
        /// <summary>標準料金表そのまま。</summary>
        public static readonly PricingPlan Default = new(false, null, null, null, null, null);

        /// <summary>割引が設定されているか。</summary>
        public bool HasDiscount => (DiscountPercent ?? 0) > 0 || (DiscountJpy ?? 0) > 0;

        /// <summary>種別に適用する料金表（独自があればそれ、無ければ標準）。</summary>
        public IReadOnlyList<PriceTier> TiersFor(SubjectType subjectType) => subjectType switch
        {
            SubjectType.B2B => B2BTiers ?? PricingTable.B2B,
            SubjectType.B2C => B2CTiers ?? PricingTable.B2C,
            _ => PricingTable.For(subjectType), // ArgumentOutOfRangeException
        };

        /// <summary>種別の無料枠（独自料金表があればそちらの値）。</summary>
        public int FreeTierMau(SubjectType subjectType) => PricingTable.FreeTierMau(TiersFor(subjectType));

        /// <summary>種別に独自料金表が設定されているか。</summary>
        public bool HasCustomTiers(SubjectType subjectType) => subjectType switch
        {
            SubjectType.B2B => B2BTiers != null,
            SubjectType.B2C => B2CTiers != null,
            _ => false,
        };
    }
}
