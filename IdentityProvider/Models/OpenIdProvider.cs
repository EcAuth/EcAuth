using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("open_id_provider")]
    public class OpenIdProvider
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }
        [Column("name")]
        public string Name { get; set; }
        [Column("idp_client_id")]
        public string IdpClientId { get; set; }
        [Column("idp_client_secret")]
        public string IdpClientSecret { get; set; }
        [Column("discovery_document_uri")]
        public string? DiscoveryDocumentUri { get; set; }
        [Column("issuer")]
        public string? Issuer { get; set; }
        [Column("authorization_endpoint")]
        public string? AuthorizationEndpoint { get; set; }
        [Column("token_endpoint")]
        public string? TokenEndpoint { get; set; }
        [Column("userinfo_endpoint")]
        public string? UserinfoEndpoint { get; set; }
        [Column("jwks_uri")]
        public string? JwksUri { get; set; }
        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public Client? Client { get; set; }
        [Column("client_id")]
        public int? ClientId { get; set; }
        public ICollection<OpenIdProviderScope> Scopes { get; } = new List<OpenIdProviderScope>();
    }
}
