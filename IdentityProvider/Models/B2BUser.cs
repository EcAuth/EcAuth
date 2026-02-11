using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("b2b_user")]
    public class B2BUser : ISubjectProvider
    {
        public const int ExternalIdMaxLength = 255;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        [Column("external_id")]
        [MaxLength(ExternalIdMaxLength)]
        [Required]
        public string ExternalId { get; set; } = string.Empty;

        [Column("user_type")]
        [MaxLength(50)]
        [Required]
        public string UserType { get; set; } = "admin";

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<B2BPasskeyCredential> PasskeyCredentials { get; } = new List<B2BPasskeyCredential>();
    }
}
