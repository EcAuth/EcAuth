using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Models
{
    /// <summary>
    /// <see cref="B2BUser"/>（「人」）に対する「発行元ごとの識別子」（EcAuthDocs#110）。
    ///
    /// 1 人の管理者が EC-CUBE インスタンス・WordPress インスタンス・企業 IdP といった
    /// 複数の発行元から到達しうるため、識別子を B2BUser 本体から分離して 1:N で持つ。
    /// これにより、企業SSO を後から導入しても既存管理者が別ユーザーとして再作成されない
    /// （どの経路で入っても sub が同じになる）。
    ///
    /// 一意性は <c>(issuer_key, external_id)</c> の複合で担保する。<c>external_id</c> は
    /// 発行元をまたぐと衝突しうる（EC-CUBE の member_id=1 と WordPress の user_id=1 は
    /// 正規化 + SHA-256 の結果が同一になる）が、<c>issuer_key</c> が名前空間として分離する。
    /// 一意制約に organization_id を含めないのは、<see cref="B2BIssuerKey"/> がグローバル一意な
    /// 値（client_id / IdP テナント ID）から構成されるため、Organization のスコープが
    /// issuer_key の中に既に含まれているから。
    /// </summary>
    [Table("b2b_user_identity")]
    public class B2BUserIdentity
    {
        /// <summary>
        /// issuer_key の最大長。<c>"client:" + <see cref="Client.ClientId"/>(512)</c> を収める。
        /// </summary>
        public const int IssuerKeyMaxLength = 520;

        /// <summary>
        /// external_id の最大長。<see cref="B2BUser.ExternalIdMaxLength"/> と揃える。
        /// 実際に格納されるのは <see cref="Services.ExternalIdHasher"/> による 64 文字の
        /// 大文字 hex だが、既存 b2b_user.external_id からの移送を素直に行うため幅を合わせる。
        /// </summary>
        public const int ExternalIdMaxLength = B2BUser.ExternalIdMaxLength;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// 紐づく <see cref="B2BUser"/> の subject（UUID）。
        /// </summary>
        [Column("b2b_subject")]
        [MaxLength(255)]
        [Required]
        public string B2BSubject { get; set; } = string.Empty;

        /// <summary>
        /// 発行元識別子。値の構成は <see cref="B2BIssuerKey"/> を参照。
        /// </summary>
        [Column("issuer_key")]
        [MaxLength(IssuerKeyMaxLength)]
        [Unicode(false)]
        [Required]
        public string IssuerKey { get; set; } = string.Empty;

        /// <summary>
        /// 発行元における不変キー。個人情報を含み得るため正規化 + SHA-256 ハッシュで保持し、
        /// 平文は持たない（<see cref="Services.ExternalIdHasher"/>）。
        /// </summary>
        [Column("external_id")]
        [MaxLength(ExternalIdMaxLength)]
        [Required]
        public string ExternalId { get; set; } = string.Empty;

        /// <summary>
        /// 補助カラム。Client 由来の発行元の場合に <see cref="Client.ClientId"/> を保持する
        /// （企業SSO 等 Client に紐づかない発行元では null）。
        ///
        /// issuer_key を文字列パースして client_id を取り出す方式は採らない。Client 解約時の
        /// 一括無効化に使う値であり、パース失敗が「解約した Client のクレデンシャルが残る」という
        /// 安全性の問題に直結するため（EcAuthDocs#110 / #111）。
        ///
        /// FK は張らない。Client 削除時に identity を道連れにせず、無効化対象として残す必要があるため。
        /// </summary>
        [Column("client_id")]
        [MaxLength(512)]
        [Unicode(false)]
        public string? ClientId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public B2BUser? B2BUser { get; set; }
    }
}
