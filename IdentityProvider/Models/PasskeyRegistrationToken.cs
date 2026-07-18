using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 申込確認（confirm）直後の「初回パスキー登録」を認可する一回限りのトークン。
    /// accounts の管理コンソールは public client（client_secret なし）のため、登録 API を
    /// client_secret で認可できない。代わりに confirm がこのトークンを発行し、
    /// register/options・register/verify がトークン検証で登録対象アカウント（Subject）を確定する。
    ///
    /// 平文トークンは保存せず SHA-256 ハッシュ（token_hash）のみ保存する。テナント横断で
    /// 検証するため EcAuthDbContext ではテナントクエリフィルターを設定しない（MagicLoginToken と同様）。
    /// </summary>
    [Table("passkey_registration_token")]
    public class PasskeyRegistrationToken
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>登録対象アカウントの統一 Subject（Account.Subject == B2BUser.Subject）。</summary>
        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        [Column("token_hash")]
        [MaxLength(128)]
        [Required]
        public string TokenHash { get; set; } = string.Empty;

        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; }

        [Column("used_at")]
        public DateTimeOffset? UsedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
