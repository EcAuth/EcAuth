using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// EcAuth サービス利用者（組織オーナー）を表すエンティティ。
    /// accounts / stg-accounts Organization に所属し、Subject は同じ Organization の
    /// B2BUser.Subject と 1:1 で共有する（既存 B2B パスキー認証機構を流用するため）。
    /// </summary>
    [Table("account")]
    public class Account : ISubjectProvider
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        [Column("email")]
        [MaxLength(255)]
        [Required]
        public string Email { get; set; } = string.Empty;

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("display_name")]
        [MaxLength(255)]
        public string? DisplayName { get; set; }

        [Column("email_verified_at")]
        public DateTimeOffset? EmailVerifiedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Organization? Organization { get; set; }
        public ICollection<AccountOrganization> ManagedOrganizations { get; }
            = new List<AccountOrganization>();
    }
}
