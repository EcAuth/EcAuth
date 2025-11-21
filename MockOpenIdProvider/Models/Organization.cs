using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MockOpenIdProvider.Models
{
    [Table("organization")]
    public class Organization
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string Name { get; set; }

        [Column("tenant_name")]
        public string TenantName { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        // Navigation properties
        public ICollection<MockIdpUser> Users { get; } = new List<MockIdpUser>();
        public ICollection<Client> Clients { get; } = new List<Client>();
        public ICollection<AuthorizationCode> AuthorizationCodes { get; } = new List<AuthorizationCode>();
        public ICollection<AccessToken> AccessTokens { get; } = new List<AccessToken>();
        public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();
    }
}
