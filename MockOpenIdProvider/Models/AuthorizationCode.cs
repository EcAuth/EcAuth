using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MockOpenIdProvider.Models
{
    [Table("authorization_code")]
    public class AuthorizationCode
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        [Column("code")]
        public string Code { get; set; }
        [Column("expires_in")]
        public int ExpiresIn { get; set; }
        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
        [Column("used")]
        public bool Used { get; set; }
        [Column("client_id")]
        public int ClientId { get; set; }
        public Client Client { get; set; }
        [Column("user_id")]
        public int UserId { get; set; }
        public MockIdpUser User { get; set; }
        [Column("organization_id")]
        public int OrganizationId { get; set; }
        public Organization Organization { get; set; }
    }
}
