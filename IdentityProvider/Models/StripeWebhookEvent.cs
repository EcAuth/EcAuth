using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 処理済みの Stripe Webhook イベント（EcAuthDocs#119）。
    /// Stripe は配信失敗時に同じイベントを再送し、成功時でも重複配信しうるため、
    /// イベント ID を主キーにした INSERT の成否で「初回か再送か」を判定する（冪等化の本体）。
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
    }
}
