using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 月次請求の内訳（EcAuthDocs#119）。起票時点の見込み額（<c>IBillingService.Estimate</c>）の写しで、
    /// Client ごとの行（<see cref="KindClient"/>、請求対象外の Client も金額 0 で残す）と、割引があるときの
    /// 割引の行（<see cref="KindDiscount"/>、負の金額）を持つ。後から Client の名前や料金設定が変わっても、
    /// 何をいくらで請求したかをこの行で説明できるようにする。
    /// <para>
    /// Stripe に InvoiceItem として送るのは <see cref="AmountJpy"/> が 0 でない行だけ（税抜）。
    /// </para>
    /// </summary>
    [Table("billing_invoice_line")]
    public class BillingInvoiceLine
    {
        public const string KindClient = "client";
        public const string KindDiscount = "discount";

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("billing_invoice_id")]
        public int BillingInvoiceId { get; set; }

        /// <summary><see cref="KindClient"/> または <see cref="KindDiscount"/>。</summary>
        [Column("kind")]
        [MaxLength(16)]
        [Required]
        public string Kind { get; set; } = KindClient;

        /// <summary>表示順（起票時の見込み額の並び）。</summary>
        [Column("position")]
        public int Position { get; set; }

        [Column("organization_id")]
        public int? OrganizationId { get; set; }

        // 内訳は監査用の写しなので、元の列（organization.code / name、client.client_id / app_name）と同じ型にして
        // 切り詰めない。短くすると、長い値を持つ Client が 1 つあるだけでその Account の行を保存できず、請求全体が止まる。
        [Column("organization_code")]
        public string? OrganizationCode { get; set; }

        [Column("organization_name")]
        public string? OrganizationName { get; set; }

        /// <summary><c>client.id</c>（内部 ID）。</summary>
        [Column("client_db_id")]
        public int? ClientDbId { get; set; }

        /// <summary><c>client.client_id</c>（公開 ID）。</summary>
        [Column("client_id")]
        [MaxLength(512)]
        [Unicode(false)]
        public string? ClientId { get; set; }

        [Column("app_name")]
        public string? AppName { get; set; }

        [Column("subject_type")]
        public SubjectType? SubjectType { get; set; }

        [Column("monthly_active_users")]
        public int MonthlyActiveUsers { get; set; }

        [Column("free_tier_mau")]
        public int FreeTierMau { get; set; }

        [Column("billable_units")]
        public int BillableUnits { get; set; }

        /// <summary>料金表どおりの金額（税抜）。請求対象外でも入れる。</summary>
        [Column("list_price_jpy")]
        public long ListPriceJpy { get; set; }

        /// <summary>請求額（税抜）。請求対象外は 0、割引の行は負。</summary>
        [Column("amount_jpy")]
        public long AmountJpy { get; set; }

        /// <summary>請求対象外の理由（<c>IBillingService.ExemptReasons</c>）。</summary>
        [Column("exempt_reason")]
        [MaxLength(64)]
        public string? ExemptReason { get; set; }

        /// <summary>請求書に載せる明細の文言。</summary>
        [Column("description")]
        [MaxLength(500)]
        [Required]
        public string Description { get; set; } = string.Empty;

        /// <summary>Stripe の InvoiceItem（<c>ii_*</c>）。送らない行と、送る前は null。</summary>
        [Column("stripe_invoice_item_id")]
        [MaxLength(255)]
        public string? StripeInvoiceItemId { get; set; }

        public BillingInvoice? Invoice { get; set; }
    }
}
