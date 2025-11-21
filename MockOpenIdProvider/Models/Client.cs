using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MockOpenIdProvider.Models
{
    [Table("client")]
    public class Client
    {
        [Key]
        [Column("id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        [Column("client_id")]
        public string ClientId { get; set; }
        [Column("client_secret")]
        public string ClientSecret { get; set; }
        [Column("client_name")]
        public string ClientName { get; set; }
        [Column("redirect_uri")]
        public string RedirectUri { get; set; }
        [Column("public_key")]
        public string PublicKey { get; set; }
        [Column("private_key")]
        public string PrivateKey { get; set; }
        [Column("organization_id")]
        public int OrganizationId { get; set; }
        public Organization Organization { get; set; }
        public ICollection<MockIdpUser> Users { get; } = new List<MockIdpUser>();
    }
}
