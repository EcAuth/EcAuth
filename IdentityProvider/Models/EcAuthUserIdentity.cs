using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Models
{
    /// <summary>
    /// B2C ユーザー（<see cref="EcAuthUser"/>）に対する「発行元ごとの識別子」（EcAuthDocs#122）。
    ///
    /// B2B の <see cref="B2BUserIdentity"/> と同じ形。1 人の B2C ユーザーに、ソーシャルログイン
    /// （Google / LINE など）や EC-CUBE 会員（Phase 2）といった複数の識別子がぶら下がる。
    /// 一意性は <c>(issuer_key, external_id)</c> の複合で担保する。<c>issuer_key</c> は
    /// <see cref="B2CIssuerKey"/> の記法で、グローバル一意な値（open_id_provider.id /
    /// client_id）から作るため organization_id を含めない。
    ///
    /// 旧 <see cref="ExternalIdpMapping"/>（<c>(external_provider, external_subject)</c> が全テナント一意）
    /// の置き換え先。移行は 3 リリースで行う（expand → transition → contract）。
    /// </summary>
    [Table("ecauth_user_identity")]
    public class EcAuthUserIdentity
    {
        /// <summary>
        /// issuer_key の最大長。<c>"client:" + <see cref="Client.ClientId"/>(512)</c> を収める
        /// （<see cref="B2BUserIdentity.IssuerKeyMaxLength"/> と同じ）。
        /// </summary>
        public const int IssuerKeyMaxLength = B2BUserIdentity.IssuerKeyMaxLength;

        /// <summary>external_id の最大長。格納されるのは 64 文字の大文字 hex。</summary>
        public const int ExternalIdMaxLength = 64;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>紐づく <see cref="EcAuthUser"/> の subject（UUID）。</summary>
        [Column("ecauth_subject")]
        [MaxLength(255)]
        [Required]
        public string EcAuthSubject { get; set; } = string.Empty;

        /// <summary>発行元識別子。値の構成は <see cref="B2CIssuerKey"/> を参照。</summary>
        [Column("issuer_key")]
        [MaxLength(IssuerKeyMaxLength)]
        [Unicode(false)]
        [Required]
        public string IssuerKey { get; set; } = string.Empty;

        /// <summary>
        /// 発行元における不変キーの SHA-256（大文字 hex）。平文は持たない。
        /// ソーシャルログインは <see cref="Services.ExternalIdHasher.HashCaseSensitive"/>（IdP の sub は
        /// 大文字小文字を区別する）、EC-CUBE 会員は <see cref="Services.ExternalIdHasher.Hash"/>。
        /// </summary>
        [Column("external_id")]
        [MaxLength(ExternalIdMaxLength)]
        [Unicode(false)]
        [Required]
        public string ExternalId { get; set; } = string.Empty;

        /// <summary>
        /// 補助カラム。識別子が属する B2C Client の <see cref="Client.ClientId"/>。
        /// Client 解約時の一括無効化に使う。issuer_key をパースしないのは B2B と同じ理由
        /// （<see cref="B2BUserIdentity.ClientId"/>）。FK は張らない。
        /// </summary>
        [Column("client_id")]
        [MaxLength(512)]
        [Unicode(false)]
        public string? ClientId { get; set; }

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public EcAuthUser? EcAuthUser { get; set; }
    }
}
