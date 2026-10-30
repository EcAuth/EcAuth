using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 月次請求の 1 件（Account × 対象月、EcAuthDocs#119）。請求書を作らなかった Account（0 円・請求対象外・
    /// 支払い方法未登録）も <see cref="Status"/> の skipped_* で行を残す（「その月に何を判断したか」の監査）。
    /// <para>
    /// <c>UNIQUE(year_month, account_subject)</c> が二重起票の防止の本体。月次ジョブは Stripe を呼ぶ前にこの行を
    /// <see cref="Services.Billing.IMonthlyInvoicingService.Statuses.Pending"/> で INSERT し、衝突したら処理しない。
    /// 金額と内訳（<see cref="Lines"/>）は INSERT 時点の値で固定し、途中で落ちたやり直しも同じ値で Stripe に送る
    /// （冪等キーが同じでパラメータが違うと Stripe はエラーにする）。
    /// </para>
    /// <para>
    /// 金額はすべて円。<see cref="SubtotalJpy"/> / <see cref="DiscountJpy"/> / <see cref="TotalJpy"/> は EcAuth が計算した税抜、
    /// <see cref="TaxJpy"/> / <see cref="StripeTotalJpy"/> / <see cref="AmountPaidJpy"/> は Stripe が計算した値を Webhook で写す。
    /// テナント横断（クエリフィルター対象外）。Account を消しても請求の記録は残すため、Account への外部キーは張らない。
    /// </para>
    /// </summary>
    [Table("billing_invoice")]
    public class BillingInvoice
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>対象月（<c>YYYY-MM</c>、JST の暦月）。</summary>
        [Column("year_month")]
        [MaxLength(7)]
        [Required]
        public string YearMonth { get; set; } = string.Empty;

        /// <summary>請求先 Account（<c>account.subject</c>）。</summary>
        [Column("account_subject")]
        [MaxLength(255)]
        [Required]
        public string AccountSubject { get; set; } = string.Empty;

        /// <summary>Account の受付テナント（accounts = live / stg-accounts = test）。Stripe のキーの選択に使う。</summary>
        [Column("tenant_name")]
        [MaxLength(255)]
        [Required]
        public string TenantName { get; set; } = string.Empty;

        /// <summary>起票時の Stripe Customer。skipped_* では null のことがある。</summary>
        [Column("stripe_customer_id")]
        [MaxLength(255)]
        public string? StripeCustomerId { get; set; }

        /// <summary>Stripe の Invoice（<c>in_*</c>）。作成前（pending の途中）と skipped_* は null。</summary>
        [Column("stripe_invoice_id")]
        [MaxLength(255)]
        public string? StripeInvoiceId { get; set; }

        /// <summary><see cref="Services.Billing.IMonthlyInvoicingService.Statuses"/> のいずれか。</summary>
        [Column("status")]
        [MaxLength(32)]
        [Required]
        public string Status { get; set; } = string.Empty;

        [Column("subtotal_jpy")]
        public long SubtotalJpy { get; set; }

        [Column("discount_jpy")]
        public long DiscountJpy { get; set; }

        /// <summary>請求額（税抜、割引後）。</summary>
        [Column("total_jpy")]
        public long TotalJpy { get; set; }

        /// <summary>Stripe が計算した消費税額。確定（finalized）前は null。</summary>
        [Column("tax_jpy")]
        public long? TaxJpy { get; set; }

        /// <summary>Stripe の請求書の合計（税込）。確定前は null。</summary>
        [Column("stripe_total_jpy")]
        public long? StripeTotalJpy { get; set; }

        /// <summary>支払われた額。</summary>
        [Column("amount_paid_jpy")]
        public long? AmountPaidJpy { get; set; }

        /// <summary>Stripe が自動で確定させる時刻（起票時に決めて固定。やり直しでも同じ値を送る）。</summary>
        [Column("finalizes_at")]
        public DateTimeOffset? FinalizesAt { get; set; }

        /// <summary>直近の失敗の内容（pending のまま止まった理由）。成功したら消す。</summary>
        [Column("error")]
        [MaxLength(1000)]
        public string? Error { get; set; }

        /// <summary>
        /// Webhook で Stripe の Invoice を読み始めた時刻（UTC ticks）。並行した Webhook の古い読み取りが新しい結果を
        /// 上書きしないよう、これより新しい読み取りのときだけ書く（<c>account.payment_method_checked_ticks</c> と同じ規約）。
        /// </summary>
        [Column("stripe_checked_ticks")]
        public long? StripeCheckedTicks { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<BillingInvoiceLine> Lines { get; set; } = new();
    }
}
