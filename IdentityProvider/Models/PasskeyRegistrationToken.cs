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

        /// <summary>
        /// このトークンで開始した WebAuthn 登録セッション（register/options が発行した session_id）。
        /// register/verify は「トークンに束縛された session_id」でのみ受理する。
        ///
        /// トークン検証だけでは 1 つのトークンから複数の登録セッションを並行して走らせられるため、
        /// used_at の single-use が事実上機能しない（各セッションが個別にクレデンシャルを登録できる）。
        /// options 呼び出しごとに束縛先を上書きすることで、常に「最後に開始したセッション 1 つだけ」が
        /// verify できる状態に保ち、1 トークン = 最大 1 クレデンシャルを保証する。
        /// </summary>
        [Column("session_id")]
        [MaxLength(64)]
        public string? SessionId { get; set; }

        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>
        /// 使用済み日時。<see cref="ConcurrencyCheckAttribute"/> を付けることで UPDATE 文に
        /// <c>WHERE used_at IS NULL</c> 相当の条件が入り、read-then-update の TOCTOU で
        /// 二重消費が成立しなくなる（敗者は DbUpdateConcurrencyException になる）。
        /// </summary>
        [Column("used_at")]
        [ConcurrencyCheck]
        public DateTimeOffset? UsedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
