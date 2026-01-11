using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace IdentityProvider.Models
{
    [Table("b2b_passkey_credential")]
    public class B2BPasskeyCredential
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("b2b_subject")]
        [MaxLength(255)]
        [Required]
        public string B2BSubject { get; set; } = string.Empty;

        [Column("credential_id")]
        [Required]
        public byte[] CredentialId { get; set; } = Array.Empty<byte>();

        [Column("public_key")]
        [Required]
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();

        [Column("sign_count")]
        [Required]
        public uint SignCount { get; set; } = 0;

        [Column("device_name")]
        [MaxLength(255)]
        public string? DeviceName { get; set; }

        [Column("aa_guid")]
        [Required]
        public Guid AaGuid { get; set; } = Guid.Empty;

        [Column("transports")]
        [MaxLength(500)]
        public string? TransportsJson { get; set; }

        [NotMapped]
        public string[] Transports
        {
            get => string.IsNullOrEmpty(TransportsJson)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(TransportsJson) ?? Array.Empty<string>();
            set => TransportsJson = value == null || value.Length == 0
                ? null
                : JsonSerializer.Serialize(value);
        }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("last_used_at")]
        public DateTimeOffset? LastUsedAt { get; set; }

        public B2BUser? B2BUser { get; set; }
    }
}
