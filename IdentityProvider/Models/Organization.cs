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

        /// <summary>
        /// サンドボックス Org が紐づく本番 Org の Id。本番 Org では null。
        ///
        /// 「1 本番 Org あたりサンドボックスは 1 つまで」の判定根拠になる。組織コードの導出
        /// （本番コード + <c>-sandbox</c>）では、テストサイトに本番と別ドメインを使った場合
        /// （<c>shop.example.jp</c> と <c>stg.example.jp</c>）にペアを特定できないため、
        /// 関連そのものをカラムとして持つ。制約は EcAuthDbContext のフィルター付き
        /// ユニークインデックスで担保する。
        /// </summary>
        [Column("parent_organization_id")]
        public int? ParentOrganizationId { get; set; }

        /// <summary>
        /// 論理削除された日時。null なら有効。
        ///
        /// **物理削除はしない**。将来の課金は Organization 単位の集計になるため、
        /// 解約済みサイトも期間つきで残す必要がある。削除済み Org は認証系の全経路から
        /// 除外される（EcAuthDbContext のクエリフィルター、ClientResolveController、
        /// AccountService.GetManagedOrganizationsAsync）。
        /// </summary>
        [Column("deleted_at")]
        public DateTimeOffset? DeletedAt { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public Organization? ParentOrganization { get; set; }
        public ICollection<Organization> SandboxOrganizations { get; } = new List<Organization>();
        public ICollection<Client> Clients { get; } = new List<Client>();
        public ICollection<RsaKeyPair> RsaKeyPairs { get; } = new List<RsaKeyPair>();
    }
}
