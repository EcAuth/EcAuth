using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("ecauth_user")]
    public class EcAuthUser : ISubjectProvider
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// メールアドレスのハッシュ。統合の手がかりであって識別子ではない（一意ではない）。
        /// メールアドレスが無いときは空文字列。
        /// </summary>
        [Column("email_hash")]
        [MaxLength(255)]
        [Required]
        public string EmailHash { get; set; } = string.Empty;

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        /// <summary>
        /// ユーザープールの単位となる B2C Client の <see cref="Client.ClientId"/>（EcAuthDocs#122）。
        /// 別サイト（別 Client）の同じ顧客は別ユーザーになる。識別モデル導入前に作られ、
        /// backfill で Client を一意に決められなかった行だけ null が残る。FK は張らない
        /// （<see cref="EcAuthUserIdentity.ClientId"/> と同じ扱い）。
        /// </summary>
        [Column("client_id")]
        [MaxLength(512)]
        [Microsoft.EntityFrameworkCore.Unicode(false)]
        public string? ClientId { get; set; }

        /// <summary>
        /// <see cref="EmailHash"/> のメールアドレスが確認済みになった時刻。null は未確認。
        /// 確認済みメールアドレス同士だけ自動で統合する（未確認で統合すると、他人のメールアドレスで
        /// IdP アカウントを作って既存会員として入れてしまうため）。
        /// </summary>
        [Column("email_verified_at")]
        public DateTimeOffset? EmailVerifiedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<ExternalIdpMapping> ExternalIdpMappings { get; } = new List<ExternalIdpMapping>();
        public ICollection<EcAuthUserIdentity> Identities { get; } = new List<EcAuthUserIdentity>();
    }
}