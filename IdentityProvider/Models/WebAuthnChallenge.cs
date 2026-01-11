using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
