using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    [Table("open_id_provider_scope")]
    public class OpenIdProviderScope
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }
        [Column("open_id_provider_id")]
        public int OpenIdProviderId { get; set; }
        public OpenIdProvider OpenIdProvider { get; set; }
        [Column("scope")]
        public string Scope { get; set; }
        [Column("is_enabled")]
        public bool IsEnabled { get; set; }
        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
