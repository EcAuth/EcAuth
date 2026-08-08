using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace IdentityProvider.Models
{
    [Table("webauthn_challenge")]
    public class WebAuthnChallenge
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("challenge")]
        [MaxLength(500)]
        [Required]
        public string Challenge { get; set; } = string.Empty;

        [Column("session_id")]
        [MaxLength(255)]
        [Required]
        public string SessionId { get; set; } = string.Empty;

        [Column("type")]
        [MaxLength(50)]
        [Required]
        public string Type { get; set; } = string.Empty;

        [Column("user_type")]
        [MaxLength(50)]
        [Required]
        public string UserType { get; set; } = string.Empty;

        [Column("subject")]
        [MaxLength(255)]
        public string? Subject { get; set; }

        [Column("rp_id")]
        [MaxLength(255)]
        public string? RpId { get; set; }

        [Column("client_id")]
        [Required]
        public int ClientId { get; set; }

        /// <summary>
        /// このセッションで発行した allowCredentials の credential_id（Base64URL 形式）を JSON 配列で保持する。
        /// WebAuthn Level 3 §7.2 Step 5「pkOptions.allowCredentials が空でない場合、credential.id が
        /// その一覧のいずれかを指すことを検証する」を verify 時に実施するための束縛先。
        ///
        /// 値の意味を 3 状態で使い分ける:
        /// - NULL: 発行時に記録していない（登録チャレンジ、およびこのカラム追加前の既存行）→ Step 5 は適用不可
        /// - "[]": allowCredentials が空で発行された（discoverable credential フロー）→ Step 5 は「空でない場合」の
        ///   条件を満たさないため適用しない
        /// - 要素あり: verify 時に照合する
        /// </summary>
        [Column("allowed_credential_ids")]
        public string? AllowedCredentialIdsJson { get; set; }

        /// <summary>
        /// <see cref="AllowedCredentialIdsJson"/> のリスト表現。未記録の場合は null。
        /// </summary>
        [NotMapped]
        public IReadOnlyList<string>? AllowedCredentialIds
        {
            get => AllowedCredentialIdsJson == null
                ? null
                : JsonSerializer.Deserialize<List<string>>(AllowedCredentialIdsJson) ?? new List<string>();
            set => AllowedCredentialIdsJson = value == null
                ? null
                : JsonSerializer.Serialize(value);
        }

        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [NotMapped]
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

        public Client? Client { get; set; }
    }
}
