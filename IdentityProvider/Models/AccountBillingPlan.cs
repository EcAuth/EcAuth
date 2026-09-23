using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// Account ごとの課金条件（EcAuthDocs#119）。無ければ標準の料金表がそのまま適用される。
    /// <para>
    /// 3 つの調整をここに集める: 課金しない（<see cref="BillingExempt"/>）、割引
    /// （<see cref="DiscountPercent"/> または <see cref="DiscountJpy"/>、併用不可）、独自料金表
    /// （<see cref="B2BTiersJson"/> / <see cref="B2CTiersJson"/>）。すべて EcAuth 側で計算し、Stripe には
    /// 最終金額だけを渡す（Stripe の Coupon / Price は使わない。見込み額と請求額を 1 箇所で出すため）。
    /// </para>
    /// <para>
    /// Account と 1:1。設定は運用 CLI（ConsoleApp <c>billing-plan</c>）で行い、管理 UI は設けない。
    /// 有効期間は「対象月の開始時点（JST 1 日 0:00）で有効か」で判定する
    /// （<c>ValidFrom &lt;= month.Start &lt; ValidUntil</c>）。月の途中で切り替わる日割りはしない。
    /// テナント横断（クエリフィルター対象外）。
    /// </para>
    /// </summary>
    [Table("account_billing_plan")]
    public class AccountBillingPlan
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>対象 Account（<c>account.subject</c>）。1 Account に 1 行。</summary>
        [Column("account_subject")]
        [MaxLength(255)]
        [Required]
        public string AccountSubject { get; set; } = string.Empty;

        /// <summary>true なら請求しない（見込み額も 0、Invoice も起票しない）。理由は <see cref="ExemptReason"/>。</summary>
        [Column("billing_exempt")]
        public bool BillingExempt { get; set; }

        /// <summary>課金しない理由（運用メモ。例: 社内利用、パートナー契約）。</summary>
        [Column("exempt_reason")]
        [MaxLength(255)]
        public string? ExemptReason { get; set; }

        /// <summary>Account 合計に対する割引率（0〜100）。<see cref="DiscountJpy"/> とは併用不可。</summary>
        [Column("discount_percent")]
        public int? DiscountPercent { get; set; }

        /// <summary>Account 合計から引く定額（円、税込）。合計を超える分は切り捨て（0 円止まり）。</summary>
        [Column("discount_jpy")]
        public long? DiscountJpy { get; set; }

        /// <summary>
        /// B2B の独自料金表（JSON、<see cref="Services.Billing.PricingTable.ParseTiers"/> の形式）。null なら標準。
        /// </summary>
        [Column("b2b_tiers_json")]
        [MaxLength(2000)]
        public string? B2BTiersJson { get; set; }

        /// <summary>B2C の独自料金表（JSON）。null なら標準。</summary>
        [Column("b2c_tiers_json")]
        [MaxLength(2000)]
        public string? B2CTiersJson { get; set; }

        /// <summary>この行が効き始める時刻。null なら過去すべて。</summary>
        [Column("valid_from")]
        public DateTimeOffset? ValidFrom { get; set; }

        /// <summary>この行が効かなくなる時刻（この時刻を含まない）。null なら無期限。</summary>
        [Column("valid_until")]
        public DateTimeOffset? ValidUntil { get; set; }

        /// <summary>運用メモ（誰との合意か、根拠の Issue など）。</summary>
        [Column("note")]
        [MaxLength(1000)]
        public string? Note { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Account? Account { get; set; }
    }
}
