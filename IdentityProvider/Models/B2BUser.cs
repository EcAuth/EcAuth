using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("b2b_user")]
    public class B2BUser : ISubjectProvider
    {
        public const int ExternalIdMaxLength = 255;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// 発行元における不変キー（正規化 + SHA-256 ハッシュ）。
        ///
        /// EcAuthDocs#110 により、識別子の正となる置き場は <see cref="B2BUserIdentity"/> へ移した。
        /// 本カラムは移行期間中のフォールバック経路として残しており、identity 側での解決に
        /// 失敗した場合にのみ参照される。移行完了後に削除する。
        /// </summary>
        [Column("external_id")]
        [MaxLength(ExternalIdMaxLength)]
        [Required]
        public string ExternalId { get; set; } = string.Empty;

        [Column("user_type")]
        [MaxLength(50)]
        [Required]
        public string UserType { get; set; } = "admin";

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<B2BPasskeyCredential> PasskeyCredentials { get; } = new List<B2BPasskeyCredential>();

        /// <summary>
        /// 発行元ごとの識別子。1 人が EC-CUBE / WordPress / 企業SSO など複数の発行元から
        /// 到達しうるため 1:N で持つ（EcAuthDocs#110）。
        /// </summary>
        public ICollection<B2BUserIdentity> Identities { get; } = new List<B2BUserIdentity>();
    }
}
