using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("access_token")]
    public class AccessToken
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Column("token")]
        [Required]
        [MaxLength(512)]
        public string Token { get; set; } = string.Empty;

        [Column("expires_at")]
        [Required]
        public DateTime ExpiresAt { get; set; }

        [Column("client_id")]
        [Required]
        public int ClientId { get; set; }

        [ForeignKey(nameof(ClientId))]
        public Client Client { get; set; } = null!;

        /// <summary>
        /// 統一Subject（B2C/B2B/Account共通）
        /// </summary>
        [Column("subject")]
        [Required]
        [MaxLength(255)]
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Subjectの種類（B2C=0, B2B=1, Account=2）
        /// </summary>
        [Column("subject_type")]
        [Required]
        public SubjectType SubjectType { get; set; }

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; }

        [Column("scopes")]
        [MaxLength(1000)]
        public string? Scopes { get; set; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}