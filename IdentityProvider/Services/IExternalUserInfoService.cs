using System.Text.Json;

namespace IdentityProvider.Services
{
    public interface IExternalUserInfoService
    {
        /// <summary>
        /// 外部IdPユーザー情報取得のためのリクエストデータ
        /// </summary>
        public class GetExternalUserInfoRequest
        {
            public string EcAuthSubject { get; set; } = string.Empty;
            public string ExternalProvider { get; set; } = string.Empty;
        }

        /// <summary>
        /// 外部IdPのUserInfo endpointから取得した生のユーザー情報
        /// </summary>
        public class ExternalUserInfo
        {
            public JsonDocument UserInfoClaims { get; set; } = JsonDocument.Parse("{}");
            public string ExternalProvider { get; set; } = string.Empty;
        }

        /// <summary>
        /// 外部IdPのUserInfo endpointからユーザー情報を取得する
        /// </summary>
        /// <param name="request">ユーザー情報取得リクエスト</param>
        /// <returns>外部IdPから取得したユーザー情報。取得失敗時はnull。</returns>
        Task<ExternalUserInfo?> GetExternalUserInfoAsync(GetExternalUserInfoRequest request);
    }
}
