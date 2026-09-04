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
            /// 外部ID（EC-CUBEのlogin_id等）- 必須
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
            /// 外部ID（null の場合は更新しない）
            /// </summary>
            public string? ExternalId { get; set; }

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
        /// 外部IDでB2Bユーザーを取得する
        ///
        /// EcAuthDocs#110 により、識別子の正となる置き場は b2b_user_identity へ移した。
        /// 本メソッドは移行期間中のフォールバック経路であり、通常は
        /// <see cref="GetByIdentityAsync"/> を先に試すこと。
        /// </summary>
        /// <param name="externalId">外部ID（EC-CUBEのlogin_id等）</param>
        /// <param name="organizationId">Organization ID</param>
        /// <returns>B2Bユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> GetByExternalIdAsync(string externalId, int organizationId);

        /// <summary>
        /// 発行元識別子と外部IDでB2Bユーザーを取得する（EcAuthDocs#110）。
        ///
        /// external_id は発行元をまたぐと衝突しうるため、必ず issuer_key と組で引く。
        /// </summary>
        /// <param name="issuerKey">発行元識別子（<see cref="Models.B2BIssuerKey"/>）</param>
        /// <param name="externalId">発行元における不変キー（平文。内部でハッシュ化する）</param>
        /// <returns>B2Bユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> GetByIdentityAsync(string issuerKey, string externalId);

        /// <summary>
        /// 旧 b2b_user.external_id 経由のフォールバック検索（EcAuthDocs#110）。
        /// 指定の発行元が引き継いでよいユーザーだけを返す。
        ///
        /// <see cref="GetByExternalIdAsync"/> は Organization 単位でしか絞れないため、
        /// 同一 Organization に発行元の異なる Client がぶら下がる構成では
        /// 「別の発行元のユーザー」を返してしまう。呼び出し元はその戻り値に対して
        /// <see cref="EnsureIdentityAsync"/> を実行するため、別人が 1 つの b2b_subject へ
        /// 恒久的に統合される（本移行が防ごうとしている衝突そのもの）。
        ///
        /// そこで返す対象を次のいずれかに限定する:
        /// <list type="bullet">
        ///   <item>identity 行を 1 つも持たない（移行対象外だった未移行ユーザー）</item>
        ///   <item>既に <paramref name="issuerKey"/> の identity を持つ（自分の発行元のユーザー）</item>
        /// </list>
        /// </summary>
        /// <param name="externalId">外部ID（平文。内部でハッシュ化する）</param>
        /// <param name="organizationId">Organization ID</param>
        /// <param name="issuerKey">引き継ぎ元となる発行元識別子</param>
        /// <returns>引き継ぎ可能な B2Bユーザー（存在しない場合は null）</returns>
        Task<B2BUser?> GetUnclaimedByExternalIdAsync(
            string externalId, int organizationId, string issuerKey);

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
        /// Organization内のB2Bユーザー数を取得する（課金・制限チェック用）
        /// </summary>
        /// <param name="organizationId">Organization ID</param>
        /// <returns>ユーザー数</returns>
        Task<int> CountByOrganizationAsync(int organizationId);
    }
}
