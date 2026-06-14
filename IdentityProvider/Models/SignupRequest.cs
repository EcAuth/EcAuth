using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// EcAuth サービスの申込確認トークンを管理するエンティティ。
    /// 申込フォーム送信時に確認メール用のトークンを発行し、メール内リンクで確認が
    /// 完了するまで Organization / Account を作成しないための一時レコード。
    /// 申込段階では Organization が未作成のため、テナント識別は所属 Organization 経由ではなく
    /// 申込を受け付けたテナント名 (accounts / stg-accounts) を表す TenantName カラムで直接行う。
    /// </summary>
    [Table("signup_request")]
    public class SignupRequest
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// 確認メールに埋め込むトークンの SHA-256 ハッシュ（16 進小文字、64 文字）。
        /// 生トークンはメール URL にのみ使用し、DB にはハッシュのみを保存する。値の生成はサービス層が担当する。
        /// </summary>
        [Column("confirm_token_hash")]
        [MaxLength(64)]
        [Required]
        public string ConfirmTokenHash { get; set; } = string.Empty;

        [Column("email")]
        [MaxLength(255)]
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Column("organization_name")]
        [MaxLength(100)]
        [Required]
        public string OrganizationName { get; set; } = string.Empty;

        [Column("contact_name")]
        [MaxLength(255)]
        public string? ContactName { get; set; }

        [Column("production_site_url")]
        [MaxLength(2048)]
        public string? ProductionSiteUrl { get; set; }

        [Column("test_site_url")]
        [MaxLength(2048)]
        public string? TestSiteUrl { get; set; }

        /// <summary>
        /// 利用中の EC-CUBE バージョン。"2" / "4" / "other" のいずれか。
        /// </summary>
        [Column("ec_cube_version")]
        [MaxLength(20)]
        [Required]
        public string EcCubeVersion { get; set; } = string.Empty;

        /// <summary>
        /// 同意した利用規約バージョン。
        /// </summary>
        [Column("terms_version")]
        [MaxLength(50)]
        public string TermsVersion { get; set; } = string.Empty;

        /// <summary>
        /// 同意したプライバシーポリシーバージョン。
        /// </summary>
        [Column("privacy_version")]
        [MaxLength(50)]
        public string PrivacyVersion { get; set; } = string.Empty;

        /// <summary>
        /// 同意した Cookie ポリシーバージョン。
        /// </summary>
        [Column("cookie_version")]
        [MaxLength(50)]
        public string CookieVersion { get; set; } = string.Empty;

        /// <summary>
        /// 申込を受け付けたテナント名 (accounts / stg-accounts)。
        /// 申込段階では Organization が未作成のため、このカラムでテナントを直接識別する。
        /// </summary>
        [Column("tenant_name")]
        [MaxLength(255)]
        [Required]
        public string TenantName { get; set; } = string.Empty;

        /// <summary>
        /// 確認トークンの有効期限。設計上は発行から 24 時間。
        /// </summary>
        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>
        /// 確認完了時刻。未確認の場合は null。
        /// </summary>
        [Column("confirmed_at")]
        public DateTimeOffset? ConfirmedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
