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
            /// 外部ID（EC-CUBEのlogin_id等）
            /// </summary>
            public string? ExternalId { get; set; }

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
        /// </summary>
        /// <param name="externalId">外部ID（EC-CUBEのlogin_id等）</param>
        /// <param name="organizationId">Organization ID</param>
        /// <returns>B2Bユーザー（存在しない場合はnull）</returns>
        Task<B2BUser?> GetByExternalIdAsync(string externalId, int organizationId);

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
