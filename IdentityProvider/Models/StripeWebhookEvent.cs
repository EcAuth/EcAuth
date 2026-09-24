using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 処理済みの Stripe Webhook イベント（EcAuthDocs#119）。
    /// Stripe は配信失敗時に同じイベントを再送し、成功時でも重複配信しうるため、
    /// イベント ID を主キーにした行で「初回か再送か」を判定する（冪等化の本体）。
    /// 行は受信時に入れ、処理が完了したら <see cref="ProcessedAt"/> を立てる。処理中に失敗した（500 で返した）
    /// イベントは行だけ残って <see cref="ProcessedAt"/> が null のままなので、Stripe の再送で再処理される。
    /// テナント横断（クエリフィルター対象外）。行は監査にも使うので削除しない。
    /// </summary>
    [Table("stripe_webhook_event")]
    public class StripeWebhookEvent
    {
        /// <summary>Stripe のイベント ID（<c>evt_*</c>）。</summary>
        [Key]
        [Column("id")]
        [MaxLength(255)]
        public string Id { get; set; } = string.Empty;

        /// <summary>イベント種別（例: <c>checkout.session.completed</c>）。</summary>
        [Column("type")]
        [MaxLength(64)]
        [Required]
        public string Type { get; set; } = string.Empty;

        /// <summary>受信したテナント（accounts / stg-accounts）。live / test の取り違え調査用。</summary>
        [Column("tenant_name")]
        [MaxLength(255)]
        [Required]
        public string TenantName { get; set; } = string.Empty;

        [Column("received_at")]
        public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// 処理が完了した時刻。null は「受信したが処理が完了していない」（処理中、または前回の処理が失敗した）。
        /// 重複判定はこれが非 null の行だけを再送として無視する。
        /// </summary>
        [Column("processed_at")]
        public DateTimeOffset? ProcessedAt { get; set; }
    }
}
