using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("rsa_key_pair")]
    public class RsaKeyPair
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }
        [Column("kid")]
        [MaxLength(256)]
        public string Kid { get; set; }
        [Column("public_key")]
        public string PublicKey { get; set; }
        [Column("private_key")]
        public string PrivateKey { get; set; }
        [Column("is_active")]
        public bool IsActive { get; set; } = true;
        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("organization_id")]
        public int OrganizationId { get; set; }
        public Organization Organization { get; set; } = null!;
    }
}
