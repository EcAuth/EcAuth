using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2Bユーザー管理サービスのインターフェース
    /// </summary>
    public interface IB2BUserService
    {
        /// <summary>
        /// ユーザー作成リクエスト
        /// </summary>
        public class CreateUserRequest
        {
            /// <summary>
            /// Subject（UUID）- 指定された場合はそのまま使用、未指定の場合は自動生成
            /// </summary>
            public string? Subject { get; set; }

            /// <summary>
            /// 外部ID（発行元アプリケーションにおける不変キー。EC-CUBE の member_id 等）- 必須
            /// </summary>
            public string ExternalId { get; set; } = string.Empty;

            /// <summary>
            /// 発行元識別子（EcAuthDocs#110）- 必須。<see cref="Models.B2BIssuerKey"/> で構成する。
            /// </summary>
            public string IssuerKey { get; set; } = string.Empty;

            /// <summary>
            /// 補助カラム。Client 由来の発行元の場合に client_id を保持する（それ以外は null）。
            /// </summary>
            public string? ClientId { get; set; }

            /// <summary>
            /// ユーザータイプ（"admin", "staff" 等）
            /// </summary>
            public string UserType { get; set; } = "admin";

            /// <summary>
            /// Organization ID
            /// </summary>
            public int OrganizationId { get; set; }
        }

        /// <summary>
        /// ユーザー作成結果
        /// </summary>
        public class CreateUserResult
        {
            /// <summary>
            /// 作成されたユーザー
            /// </summary>
            public B2BUser User { get; set; } = null!;
        }

        /// <summary>
        /// ユーザー更新リクエスト
        /// </summary>
        public class UpdateUserRequest
        {
            /// <summary>
            /// 更新対象のSubject
            /// </summary>
            public string Subject { get; set; } = string.Empty;

            /// <summary>
            /// ユーザータイプ（null の場合は更新しない）
            /// </summary>
            public string? UserType { get; set; }
        }

        /// <summary>
        /// 新しいB2Bユーザーを作成する
        /// </summary>
        /// <param name="request">ユーザー作成リクエスト</param>
        /// <returns>作成されたユーザー</returns>
        Task<CreateUserResult> CreateAsync(CreateUserRequest request);

        /// <summary>
        /// SubjectでB2Bユーザーを取得する
        /// </summary>
        /// <param name="subject">ユーザーSubject（UUID）</param>
        /// <returns>B2Bユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> GetBySubjectAsync(string subject);

        /// <summary>
        /// 発行元識別子と外部IDでB2Bユーザーを取得する（EcAuthDocs#110）。
        ///
        /// external_id は発行元をまたぐと衝突しうるため、必ず issuer_key と組で引く。
        /// external_id による解決経路はこれだけ（旧 b2b_user.external_id へのフォールバックは
        /// リリース 5 で除去した）。
        /// </summary>
        /// <param name="issuerKey">発行元識別子（<see cref="Models.B2BIssuerKey"/>）</param>
        /// <param name="externalId">発行元における不変キー（平文。内部でハッシュ化する）</param>
        /// <returns>B2Bユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> GetByIdentityAsync(string issuerKey, string externalId);

        /// <summary>
        /// 指定の発行元における識別子行が無ければ作成する（既にあれば何もしない）。
        ///
        /// EcAuthDocs#110 の決定により、識別子が変わっても旧行は削除せず共存させる。
        /// 同一 issuer_key の下に旧 hash と新 hash が並ぶことで、プラグイン更新前に
        /// 登録済みのユーザーも解決でき、ハードカットオーバーが不要になる。
        /// </summary>
        /// <param name="subject">紐づける B2BUser の subject</param>
        /// <param name="issuerKey">発行元識別子</param>
        /// <param name="externalId">発行元における不変キー（平文。内部でハッシュ化する）</param>
        /// <param name="clientId">Client 由来の発行元の場合の client_id（それ以外は null）</param>
        Task EnsureIdentityAsync(string subject, string issuerKey, string externalId, string? clientId);

        /// <summary>
        /// B2Bユーザーを更新する
        /// </summary>
        /// <param name="request">更新リクエスト</param>
        /// <returns>更新されたユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> UpdateAsync(UpdateUserRequest request);

        /// <summary>
        /// B2Bユーザーを削除する
        /// </summary>
        /// <param name="subject">ユーザーSubject</param>
        /// <returns>削除に成功した場合true</returns>
        Task<bool> DeleteAsync(string subject);

        /// <summary>
        /// B2Bユーザーが存在するか確認する
        /// </summary>
        /// <param name="subject">ユーザーSubject</param>
        /// <returns>存在する場合true</returns>
        Task<bool> ExistsAsync(string subject);

        /// <summary>
        /// Client ごとの登録 B2B ユーザー数を取得する（利用状況の参考値、EcAuthDocs#45）。
        ///
        /// <c>b2b_user</c> は Organization 単位だが、識別子は発行元ごとの <c>b2b_user_identity</c>（EcAuthDocs#110）に
        /// 分離されているため、<c>client_id</c> で <c>COUNT(DISTINCT b2b_subject)</c> すれば Client 別に数えられる。
        /// 企業 SSO 由来の identity（<c>client_id = null</c>）はどの Client にも数えない。
        /// 課金に使うのは MAU であり、この値は参考値（EcAuthDocs#119）。
        /// </summary>
        /// <param name="clientIds">数える Client の <see cref="Client.ClientId"/>（文字列。<c>client.id</c> ではない）</param>
        /// <returns>client_id → 登録ユーザー数。identity の無い Client はキー自体が無い</returns>
        Task<IReadOnlyDictionary<string, int>> CountByClientsAsync(IReadOnlyCollection<string> clientIds);
    }
}
