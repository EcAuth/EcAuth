using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    public interface ITokenService
    {
        /// <summary>
        /// IDトークン生成のためのリクエストデータ
        /// </summary>
        public class TokenRequest
        {
            /// <summary>
            /// ユーザー情報（EcAuthUser または B2BUser）
            /// ISubjectProvider インターフェイスを実装したエンティティを渡す
            /// </summary>
            public ISubjectProvider User { get; set; } = null!;

            public Client Client { get; set; } = null!;
            public string[]? RequestedScopes { get; set; }
            public string? Nonce { get; set; }

            /// <summary>
            /// Subjectの種別（B2C=0, B2B=1, Account=2）
            /// 指定しない場合はB2C（デフォルト）として扱う
            /// </summary>
            public SubjectType SubjectType { get; set; } = SubjectType.B2C;

            /// <summary>
            /// Account（<see cref="SubjectType.Account"/>）が管理する Organization 一覧。
            /// 指定された場合、ID/Access トークンに <c>managed_orgs</c> クレームとして付与する。
            /// Account 以外の SubjectType では使用しない。
            /// </summary>
            public IReadOnlyList<IAccountService.ManagedOrganization>? ManagedOrgs { get; set; }

            /// <summary>
            /// 発行経路。<see cref="Services.GrantType.AuthorizationCode"/> のときだけ MAU に記録される（EcAuthDocs#45）。
            /// <c>required</c> で既定値を持たせないのは、refresh 経路（EcAuth#339）の実装で申告を省くと
            /// サイレントに MAU が過剰計上されるため。省略はコンパイルエラーになる。
            /// </summary>
            public required GrantType GrantType { get; init; }

            /// <summary>
            /// 認証方式（<c>monthly_active_user.auth_method</c>）。未指定なら <see cref="SubjectType"/> から推論する
            /// （B2B → <c>b2b_passkey</c>、B2C → <c>b2c_social</c>）。B2B SSO（EcAuthDocs#123）を実装する際は
            /// 推論が黙って誤記録するため、認可コードに認証方式を持たせてここへ明示的に渡すこと。
            /// </summary>
            public string? AuthMethod { get; set; }
        }

        /// <summary>
        /// トークンレスポンス
        /// </summary>
        public class TokenResponse
        {
            public string IdToken { get; set; } = string.Empty;
            public string AccessToken { get; set; } = string.Empty;
            public int ExpiresIn { get; set; }
            public string TokenType { get; set; } = "Bearer";
            public string? RefreshToken { get; set; }
        }

        /// <summary>
        /// アクセストークン検証結果
        /// </summary>
        public class AccessTokenValidationResult
        {
            public bool IsValid { get; set; }
            public string? Subject { get; set; }
            public SubjectType? SubjectType { get; set; }
            public string? ClientId { get; set; }
            public int? OrganizationId { get; set; }
            public string? Jti { get; set; }
            public string? Scopes { get; set; }
        }

        /// <summary>
        /// JWTベースのIDトークンを生成する
        /// </summary>
        /// <param name="request">トークン生成リクエスト</param>
        /// <returns>TokenResponse</returns>
        Task<TokenResponse> GenerateTokensAsync(TokenRequest request);

        /// <summary>
        /// IDトークンを生成する
        /// </summary>
        /// <param name="request">トークン生成リクエスト</param>
        /// <returns>JWT形式のIDトークン</returns>
        Task<string> GenerateIdTokenAsync(TokenRequest request);

        /// <summary>
        /// アクセストークンを生成する
        /// </summary>
        /// <param name="request">トークン生成リクエスト</param>
        /// <returns>アクセストークン</returns>
        Task<string> GenerateAccessTokenAsync(TokenRequest request);

        /// <summary>
        /// JWTトークンを検証する
        /// </summary>
        /// <param name="token">検証するJWTトークン</param>
        /// <param name="organizationId">OrganizationID</param>
        /// <returns>検証に成功した場合、ユーザーのSubject</returns>
        Task<string?> ValidateTokenAsync(string token, int organizationId);

        /// <summary>
        /// アクセストークンを検証する
        /// </summary>
        /// <param name="token">検証するアクセストークン</param>
        /// <returns>検証に成功した場合、ユーザーのSubject</returns>
        Task<string?> ValidateAccessTokenAsync(string token);

        /// <summary>
        /// アクセストークンを検証し、SubjectTypeを含む詳細な結果を返す
        /// </summary>
        /// <param name="token">検証するアクセストークン</param>
        /// <returns>検証結果（Subject、SubjectType を含む）</returns>
        Task<AccessTokenValidationResult> ValidateAccessTokenWithTypeAsync(string token);

        /// <summary>
        /// アクセストークンを無効化する（リボケーション）
        /// </summary>
        /// <param name="token">無効化するアクセストークン</param>
        /// <returns>無効化に成功したかどうか</returns>
        Task<bool> RevokeAccessTokenAsync(string token);
    }
}