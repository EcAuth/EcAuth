using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// EcAuth サービス利用者（組織オーナー）を表すエンティティ。
    /// accounts / stg-accounts Organization に所属し、Subject は同じ Organization の
    /// B2BUser.Subject と 1:1 で共有する（既存 B2B パスキー認証機構を流用するため）。
    /// </summary>
    [Table("account")]
    public class Account : ISubjectProvider
    {
        /// <summary>
        /// 1 アカウントが持てる本番サイト（本番 Organization 配下の Client）数の既定上限。
        /// 単位は Organization ではなく Client（EcAuthDocs#121 項目 3。1 Organization に複数の
        /// サイトがぶら下がるため）。サンドボックス Org は各本番に 1 つまでという別制約で縛り、
        /// その配下の Client もこの数には含めない。
        /// </summary>
        public const int DefaultMaxSites = 10;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        [Column("email")]
        [MaxLength(255)]
        [Required]
        public string Email { get; set; } = string.Empty;

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("display_name")]
        [MaxLength(255)]
        public string? DisplayName { get; set; }

        [Column("email_verified_at")]
        public DateTimeOffset? EmailVerifiedAt { get; set; }

        /// <summary>
        /// このアカウントが持てる本番サイト（本番 Organization 配下の Client）数の上限。
        /// プラン変更や個別対応は DB のこのカラムを直接更新して運用する（変更 API は設けない）。
        /// 論理削除済みの Organization 配下の Client は上限のカウント対象外。
        /// </summary>
        [Column("max_sites")]
        public int MaxSites { get; set; } = DefaultMaxSites;

        /// <summary>
        /// Stripe Customer の ID（<c>cus_*</c>）。支払い主体は Account なので 1:1（EcAuthDocs#119）。
        /// 支払い方法の初回登録（Checkout の setup モード）時に遅延作成し、それまでは null。
        /// accounts テナントは live、stg-accounts テナントは test モードの Customer を指す。
        /// </summary>
        [Column("stripe_customer_id")]
        [MaxLength(255)]
        public string? StripeCustomerId { get; set; }

        /// <summary>
        /// 支払い方法（カード）が Stripe Customer の既定の支払い方法として登録された時刻。
        /// Stripe の Webhook / 同期で更新する非正規化キャッシュで、上限強制（<c>register/options</c>）の
        /// 判定はこの列だけを見る（ホットパスから Stripe API を呼ばないため）。
        /// カードが外れて既定の支払い方法が無くなったら null に戻す。
        /// </summary>
        [Column("payment_method_registered_at")]
        public DateTimeOffset? PaymentMethodRegisteredAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<AccountOrganization> ManagedOrganizations { get; }
            = new List<AccountOrganization>();
    }
}
