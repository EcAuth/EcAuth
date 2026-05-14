using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// Account と 顧客 Organization の N:N リレーションを表す中間テーブル。
    /// テナント横断で参照されるため EcAuthDbContext ではテナントクエリフィルターを設定しない。
    /// </summary>
    [Table("account_organization")]
    public class AccountOrganization
    {
        [Column("account_subject")]
        [MaxLength(255)]
        [Required]
        public string AccountSubject { get; set; } = string.Empty;

        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        [Column("role")]
        [MaxLength(50)]
        [Required]
        public string Role { get; set; } = "owner";

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public Account? Account { get; set; }
        public Organization? Organization { get; set; }
    }
}
