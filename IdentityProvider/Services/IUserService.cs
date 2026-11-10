using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    public interface IUserService
    {
        /// <summary>
        /// 外部 IdP（ソーシャルログイン）で認証できた B2C ユーザーの解決リクエスト。
        /// </summary>
        public sealed class FederatedLoginRequest
        {
            /// <summary>ログイン先の B2C Client（ユーザープールの単位）。OrganizationId を持つこと。</summary>
            public required Client Client { get; init; }

            /// <summary>認証に使った OpenIdProvider。<see cref="Client"/> に紐づくこと。</summary>
            public required OpenIdProvider Provider { get; init; }

            /// <summary>IdP の sub（大文字小文字を区別する）。</summary>
            public required string ExternalSubject { get; init; }

            /// <summary>IdP が返したメールアドレス（無ければ null か空）。</summary>
            public string? Email { get; init; }

            /// <summary>IdP がメールアドレスを確認済みと表明しているか。</summary>
            public bool EmailVerified { get; init; }
        }

        /// <summary>
        /// JIT プロビジョニング: 外部 IdP で認証できたユーザーを解決し、無ければ作成する（EcAuthDocs#122）。
        /// 解決順は identity → 旧 ExternalIdpMapping（遅延移行）→ 確認済みメールアドレスでの統合 → 新規作成。
        /// </summary>
        /// <exception cref="UserPoolMismatchException">identity が別の Client / Organization のユーザーを指している。</exception>
        Task<EcAuthUser> ResolveFederatedUserAsync(FederatedLoginRequest request);

        /// <summary>
        /// Subjectでユーザーを取得する
        /// </summary>
        /// <param name="subject">ユーザーのSubject</param>
        /// <returns>EcAuthUser または null</returns>
        Task<EcAuthUser?> GetUserBySubjectAsync(string subject);

        /// <summary>
        /// メールハッシュでユーザーを検索する（複数組織対応）
        /// </summary>
        /// <param name="emailHash">メールアドレスのハッシュ</param>
        /// <param name="organizationId">組織ID（nullの場合は全組織）</param>
        /// <returns>ユーザーリスト</returns>
        Task<List<EcAuthUser>> GetUsersByEmailHashAsync(string emailHash, int? organizationId = null);
    }

    /// <summary>
    /// identity が要求元とは別のユーザープール（Client / Organization）のユーザーを指している。
    /// issuer_key は OpenIdProvider の行（= Client）で区切っているので通常は起きない。起きたら
    /// データの不整合なので、別プールのユーザーとしてログインさせず失敗にする。
    /// </summary>
    public sealed class UserPoolMismatchException : Exception
    {
        public UserPoolMismatchException(string message) : base(message) { }
    }
}
