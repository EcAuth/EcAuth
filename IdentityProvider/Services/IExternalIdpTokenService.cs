using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    public interface IExternalIdpTokenService
    {
        /// <summary>
        /// 外部IdPトークン保存のためのリクエストデータ
        /// </summary>
        public class SaveTokenRequest
        {
            public string EcAuthSubject { get; set; } = string.Empty;
            public string ExternalProvider { get; set; } = string.Empty;
            public string AccessToken { get; set; } = string.Empty;
            public string? RefreshToken { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }

        /// <summary>
        /// 外部IdPトークンを保存または更新する
        /// </summary>
        /// <param name="request">トークン保存リクエスト</param>
        /// <returns>保存されたExternalIdpToken</returns>
        Task<ExternalIdpToken> SaveTokenAsync(SaveTokenRequest request);

        /// <summary>
        /// 外部IdPトークンを取得する
        /// </summary>
        /// <param name="ecAuthSubject">EcAuthのユーザーSubject</param>
        /// <param name="externalProvider">外部IdPのプロバイダー名</param>
        /// <returns>有効なトークンが存在する場合、ExternalIdpToken。存在しない場合、null。</returns>
        Task<ExternalIdpToken?> GetTokenAsync(string ecAuthSubject, string externalProvider);

        /// <summary>
        /// 外部IdPトークンをリフレッシュする
        /// </summary>
        /// <param name="ecAuthSubject">EcAuthのユーザーSubject</param>
        /// <param name="externalProvider">外部IdPのプロバイダー名</param>
        /// <returns>リフレッシュされたトークン。リフレッシュに失敗した場合、null。</returns>
        Task<ExternalIdpToken?> RefreshTokenAsync(string ecAuthSubject, string externalProvider);

        /// <summary>
        /// 期限切れトークンをクリーンアップする
        /// </summary>
        /// <returns>削除されたトークン数</returns>
        Task<int> CleanupExpiredTokensAsync();
    }
}
