using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("external_idp_token")]
    public class ExternalIdpToken
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Column("ecauth_subject")]
        [Required]
        [MaxLength(255)]
        public string EcAuthSubject { get; set; } = string.Empty;

        [ForeignKey(nameof(EcAuthSubject))]
        public EcAuthUser EcAuthUser { get; set; } = null!;

        [Column("external_provider")]
        [Required]
        [MaxLength(100)]
        public string ExternalProvider { get; set; } = string.Empty;

        [Column("access_token")]
        [Required]
        [MaxLength(2048)]
        public string AccessToken { get; set; } = string.Empty;

        [Column("refresh_token")]
        [MaxLength(2048)]
        public string? RefreshToken { get; set; }

        [Column("expires_at")]
        [Required]
        public DateTimeOffset ExpiresAt { get; set; }

        [Column("created_at")]
        [Required]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        [Required]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}
