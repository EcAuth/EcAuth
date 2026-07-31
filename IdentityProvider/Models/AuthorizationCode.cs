using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("authorization_code")]
    public class AuthorizationCode
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        [Column("code")]
        [MaxLength(255)]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 統一Subject（B2C/B2B/Account共通）
        /// </summary>
        [Column("subject")]
        [Required]
        [MaxLength(255)]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Subjectの種別（B2C=0, B2B=1, Account=2）
        /// </summary>
        [Column("subject_type")]
        [Required]
        public SubjectType SubjectType { get; set; }

        [Column("client_id")]
        [Required]
        public int ClientId { get; set; }

        [Column("redirect_uri")]
        [MaxLength(2000)]
        [Required]
        public string RedirectUri { get; set; } = string.Empty;

        [Column("scope")]
        [MaxLength(500)]
        public string? Scope { get; set; }

        [Column("state")]
        [MaxLength(500)]
        public string? State { get; set; }

        /// <summary>
        /// PKCE (RFC 7636) の code_challenge。認可リクエスト時に public client から
        /// 受け取り、トークン交換時に code_verifier と突き合わせる。未使用（confidential
        /// 経路など）の場合は null。
        /// </summary>
        [Column("code_challenge")]
        [MaxLength(128)]
        public string? CodeChallenge { get; set; }

        /// <summary>
        /// PKCE の code_challenge_method。本 IdP は "S256" のみサポートする。
        /// CodeChallenge が null のときは null。
        /// </summary>
        [Column("code_challenge_method")]
        [MaxLength(10)]
        public string? CodeChallengeMethod { get; set; }

        [Column("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }

        [Column("is_used")]
        public bool IsUsed { get; set; } = false;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("used_at")]
        public DateTimeOffset? UsedAt { get; set; }

        public Client? Client { get; set; }
    }
}