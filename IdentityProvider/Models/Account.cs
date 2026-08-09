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
        /// 1 アカウントが持てる本番 Organization 数の既定上限。
        /// サンドボックス Org は各本番に 1 つまでという別制約で縛るため、この数には含めない。
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
        /// このアカウントが持てる本番 Organization 数の上限。
        /// プラン変更や個別対応は DB のこのカラムを直接更新して運用する（変更 API は設けない）。
        /// 論理削除済みの Organization は上限のカウント対象外。
        /// </summary>
        [Column("max_sites")]
        public int MaxSites { get; set; } = DefaultMaxSites;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<AccountOrganization> ManagedOrganizations { get; }
            = new List<AccountOrganization>();
    }
}
