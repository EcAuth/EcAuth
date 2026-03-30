using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("organization")]
    public class Organization
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }
        [Column("code")]
        public string Code { get; set; }
        [Column("name")]
        public string Name { get; set; }
        [Column("tenant_name")]
        public string? TenantName { get; set; }
        [Column("is_sandbox")]
        public bool IsSandbox { get; set; } = false;
        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public ICollection<Client> Clients { get; } = new List<Client>();
        public ICollection<RsaKeyPair> RsaKeyPairs { get; } = new List<RsaKeyPair>();
    }
}
