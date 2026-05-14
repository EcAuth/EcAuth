using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// パスキー紛失時のリカバリ動線として、メール経由のマジックリンクログインを実現するトークン。
    /// 平文トークンは DB に保存せず、SHA-256 ハッシュ (token_hash) のみ保存する。
    /// Account 不在のリクエストもレート制限カウントの対象とするため AccountSubject は nullable。
    /// テナント横断のレート制限判定を行うため EcAuthDbContext ではテナントクエリフィルターを設定しない。
    /// </summary>
    [Table("magic_login_token")]
    public class MagicLoginToken
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("account_subject")]
        [MaxLength(255)]
        public string? AccountSubject { get; set; }

        [Column("requested_email_hash")]
        [MaxLength(64)]
        [Required]
        public string RequestedEmailHash { get; set; } = string.Empty;

        [Column("token_hash")]
        [MaxLength(128)]
        [Required]
        public string TokenHash { get; set; } = string.Empty;

        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; }

        [Column("used_at")]
        public DateTimeOffset? UsedAt { get; set; }

        [Column("requested_ip")]
        [MaxLength(45)]
        public string? RequestedIp { get; set; }

        [Column("requested_user_agent")]
        [MaxLength(1000)]
        public string? RequestedUserAgent { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Account? Account { get; set; }
    }
}
