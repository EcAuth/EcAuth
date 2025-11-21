using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MockOpenIdProvider.Models
{
    [Table("mock_idp_user")]
    public class MockIdpUser
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        [Column("email")]
        public string Email { get; set; }
        [Column("password")]
        public string Password { get; set; }
        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("organization_id")]
        public int OrganizationId { get; set; }
        public Organization Organization { get; set; }
        public Client? Client { get; set; }
        public int? ClientId { get; set; }
    }
}
