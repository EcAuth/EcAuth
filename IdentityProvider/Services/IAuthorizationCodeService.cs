using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    public interface IAuthorizationCodeService
    {
        /// <summary>
        /// 認可コード生成リクエスト
        /// </summary>
        public class AuthorizationCodeRequest
        {
            public string Subject { get; set; } = string.Empty;           // ユーザーID
            public int ClientId { get; set; }             // クライアントID
            public string RedirectUri { get; set; } = string.Empty;       // リダイレクトURI
            public string? Scope { get; set; }            // スコープ（オプション）
            public string? State { get; set; }            // Stateパラメータ（オプション）
            public int ExpirationMinutes { get; set; } = 10; // 有効期限（デフォルト10分）
            public bool IsB2B { get; set; } = false;      // B2B認証の場合true
        }

        /// <summary>
        /// 認可コードを生成する
        /// </summary>
        Task<AuthorizationCode> GenerateAuthorizationCodeAsync(AuthorizationCodeRequest request);

        /// <summary>
        /// 認可コードを取得する
        /// </summary>
        Task<AuthorizationCode?> GetAuthorizationCodeAsync(string code);

        /// <summary>
        /// 認可コードを使用済みにマークする
        /// </summary>
        Task<bool> MarkAsUsedAsync(string code);

        /// <summary>
        /// 期限切れの認可コードをクリーンアップする
        /// </summary>
        Task<int> CleanupExpiredCodesAsync();

        /// <summary>
        /// 統計情報を取得する
        /// </summary>
        Task<AuthorizationCodeStatistics> GetStatisticsAsync();
    }

    /// <summary>
    /// 認可コード統計情報
    /// </summary>
    public class AuthorizationCodeStatistics
    {
        public int TotalCodes { get; set; }
        public int ActiveCodes { get; set; }
        public int ExpiredCodes { get; set; }
        public int UsedCodes { get; set; }
    }
}