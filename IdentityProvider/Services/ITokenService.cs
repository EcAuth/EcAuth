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
            public EcAuthUser User { get; set; } = null!;
            public Client Client { get; set; } = null!;
            public string[]? RequestedScopes { get; set; }
            public string? Nonce { get; set; }

            /// <summary>
            /// B2B認証時のSubject（B2BUser.Subject）
            /// B2B認証の場合に設定される
            /// </summary>
            public string? B2BSubject { get; set; }

            /// <summary>
            /// Subjectの種別（B2C=0, B2B=1, Account=2）
            /// 指定しない場合はB2C（デフォルト）として扱う
            /// </summary>
            public SubjectType SubjectType { get; set; } = SubjectType.B2C;
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
        /// <param name="clientId">クライアントID</param>
        /// <returns>検証に成功した場合、ユーザーのSubject</returns>
        Task<string?> ValidateTokenAsync(string token, int clientId);

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