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

        [Column("email_hash")]
        [MaxLength(255)]
        [Required]
        public string EmailHash { get; set; } = string.Empty;

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<ExternalIdpMapping> ExternalIdpMappings { get; } = new List<ExternalIdpMapping>();
    }
}