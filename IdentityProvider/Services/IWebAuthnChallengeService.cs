using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// WebAuthnチャレンジ管理サービスのインターフェース
    /// </summary>
    public interface IWebAuthnChallengeService
    {
        /// <summary>
        /// チャレンジ生成リクエスト
        /// </summary>
        public class ChallengeRequest
        {
            /// <summary>
            /// チャレンジタイプ（"registration" または "authentication"）
            /// </summary>
            public string Type { get; set; } = string.Empty;

            /// <summary>
            /// ユーザータイプ（"b2b" または "b2c"）
            /// </summary>
            public string UserType { get; set; } = string.Empty;

            /// <summary>
            /// ユーザーSubject（B2B登録・B2C認証では必須、B2B認証・B2C登録ではnull許容）
            /// </summary>
            public string? Subject { get; set; }

            /// <summary>
            /// Relying Party ID（EC-CUBEサイトのドメイン）
            /// </summary>
            public string? RpId { get; set; }

            /// <summary>
            /// クライアントID
            /// </summary>
            public int ClientId { get; set; }
        }

        /// <summary>
        /// チャレンジ生成結果
        /// </summary>
        public class ChallengeResult
        {
            /// <summary>
            /// セッションID（ユニーク識別子）
            /// </summary>
            public string SessionId { get; set; } = string.Empty;

            /// <summary>
            /// チャレンジ（Base64URL形式、32バイト以上）
            /// </summary>
            public string Challenge { get; set; } = string.Empty;

            /// <summary>
            /// 有効期限（5分後）
            /// </summary>
            public DateTimeOffset ExpiresAt { get; set; }
        }

        /// <summary>
        /// チャレンジを生成する（32バイトランダム、5分有効）
        /// </summary>
        /// <param name="request">チャレンジ生成リクエスト</param>
        /// <returns>チャレンジ生成結果</returns>
        Task<ChallengeResult> GenerateChallengeAsync(ChallengeRequest request);

        /// <summary>
        /// セッションIDでチャレンジを取得する
        /// </summary>
        /// <param name="sessionId">セッションID</param>
        /// <returns>チャレンジ（期限切れまたは存在しない場合はnull）</returns>
        Task<WebAuthnChallenge?> GetChallengeBySessionIdAsync(string sessionId);

        /// <summary>
        /// チャレンジを使用済みとして削除する
        /// </summary>
        /// <param name="sessionId">セッションID</param>
        /// <returns>削除に成功した場合true</returns>
        Task<bool> ConsumeChallengeAsync(string sessionId);

        /// <summary>
        /// 期限切れチャレンジをクリーンアップする
        /// </summary>
        /// <returns>削除された件数</returns>
        Task<int> CleanupExpiredChallengesAsync();
    }
}
