using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 月間アクティブユーザー（MAU）の記録行。1 ユーザー × 1 Client × 1 月 = 1 行（EcAuthDocs#45）。
    ///
    /// 課金（EcAuthDocs#119）の一次データ。「当月に人間の認証行為（パスキー assertion / 外部 IdP 認証 / SSO）を
    /// 経てトークン発行されたユーザー」を、<see cref="Services.TokenService"/> のアクセストークン発行直後に
    /// write-once で記録する。<c>grant_type=refresh_token</c> による再発行は記録しない（MAU の定義から外れる）。
    ///
    /// <para>
    /// <b>クエリフィルターを付けない</b>。付けると accounts テナントからの横断参照（マイページの利用状況 API /
    /// ConsoleApp の請求集計）が 0 件になる。参照経路は <see cref="Services.IUsageReportService"/>
    /// （organizationIds 必須）に一本化し、構造で誤参照を防ぐ。
    /// </para>
    /// <para>
    /// <b><c>subject</c> に FK を張らない</b>。B2BUser / EcAuthUser / Account を横断する polymorphic な参照であり、
    /// かつ <see cref="Services.B2BUserService"/> は B2BUser を物理削除するため、FK を張ると課金証跡が消えるか
    /// 削除が失敗する。結果として「当月 MAU &gt; 登録ユーザー数」は正常に起こりうる。
    /// </para>
    /// <para>
    /// <b><c>organization_id</c> を非正規化して持つ</b>。Organization 単位の distinct MAU（参考値）を JOIN なしで
    /// 出すため。記録時は <see cref="Client.OrganizationId"/> が手元にあるので追加クエリは不要。
    /// </para>
    /// <para>
    /// <b><c>last_seen_at</c> は持たない</b>。MAU は distinct count なので 2 回目以降のログインは書き込みゼロ
    /// （UNIQUE index の seek のみ）で済ませる。
    /// </para>
    /// </summary>
    [Table("monthly_active_user")]
    public class MonthlyActiveUser
    {
        /// <summary>
        /// <c>year_month</c> の桁数（<c>"2026-08"</c>）。
        /// </summary>
        public const int YearMonthLength = 7;

        /// <summary>
        /// <c>auth_method</c> の最大長。
        /// </summary>
        public const int AuthMethodMaxLength = 32;

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("id")]
        public int Id { get; set; }

        /// <summary>
        /// 対象月（<c>"yyyy-MM"</c>、JST 基準）。生成・検証は <see cref="Services.UsageMonth"/> を通す。
        /// <c>char(7)</c> ではなく <c>nvarchar(7)</c> にしているのは、生 SQL のパラメータが nvarchar で送られるため
        /// （char 列だと暗黙変換で index seek が壊れうる）。
        /// </summary>
        [Column("year_month")]
        [MaxLength(YearMonthLength)]
        [Required]
        public string YearMonth { get; set; } = string.Empty;

        /// <summary>
        /// 認証した Client（<see cref="Client.Id"/>）。請求・集計の単位。
        /// </summary>
        [Column("client_id")]
        [Required]
        public int ClientId { get; set; }

        /// <summary>
        /// Client の所属 Organization（<see cref="Client.OrganizationId"/> の非正規化）。
        /// </summary>
        [Column("organization_id")]
        [Required]
        public int OrganizationId { get; set; }

        /// <summary>
        /// 統一 Subject（B2C / B2B / Account 共通）。FK は張らない（クラス doc 参照）。
        /// </summary>
        [Column("subject")]
        [MaxLength(255)]
        [Required]
        public string Subject { get; set; } = string.Empty;

        [Column("subject_type")]
        [Required]
        public SubjectType SubjectType { get; set; }

        /// <summary>
        /// 認証方式（<c>"b2b_passkey"</c> / <c>"b2c_social"</c>。将来 <c>"b2c_passkey"</c> / <c>"b2b_sso"</c>）。
        /// <see cref="SubjectType"/> だけでは将来 B2C パスキーと B2C ソーシャルを区別できず、後から列を足しても
        /// 既存行を埋め戻す手段が無いため、最初から持つ。
        /// </summary>
        [Column("auth_method")]
        [MaxLength(AuthMethodMaxLength)]
        [Required]
        public string AuthMethod { get; set; } = string.Empty;

        /// <summary>
        /// その月に最初にトークン発行された時刻（UTC）。JST 基準は <see cref="YearMonth"/> にのみ現れる。
        /// </summary>
        [Column("first_seen_at")]
        [Required]
        public DateTimeOffset FirstSeenAt { get; set; }

        public Client? Client { get; set; }
        public Organization? Organization { get; set; }
    }
}
