using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration;

namespace IdentityProvider.Security
{
    /// <summary>
    /// Host ヘッダの許可リスト（<c>AllowedHosts</c>）の補完（EcAuthDocs#102）。
    ///
    /// issuer / jwks_uri は <see cref="Services.IssuerResolver"/> がリクエストの Host から動的に
    /// 組み立てる（サブドメイン = テナント設計）。<c>AllowedHosts</c> が <c>"*"</c> だと任意の
    /// Host ヘッダで discovery の issuer / jwks_uri を攻撃者ドメインに書き換えられるため、
    /// appsettings で正規ホスト（本番: <c>*.ec-auth.io</c>）に限定している。
    ///
    /// それに加えて、App Service が自動設定する <c>WEBSITE_HOSTNAME</c>
    /// （<c>{app}.azurewebsites.net</c>）を許可する。プラットフォームの health check
    /// （<c>/healthz</c>）と staging の verify / E2E はこの既定ホストで到達するため、
    /// これを落とすとインスタンスが unhealthy 判定され evict される。値は環境ごとに異なり
    /// 実行環境が自動で与えるため、Terraform / CI / .env には配線しない。
    /// </summary>
    public static class HostFilteringSetup
    {
        /// <summary>App Service が既定ホスト名を格納する環境変数。</summary>
        public const string WebsiteHostnameKey = "WEBSITE_HOSTNAME";

        /// <summary>
        /// <see cref="HostFilteringOptions.AllowedHosts"/> に <c>WEBSITE_HOSTNAME</c> を追加する。
        /// 未設定（ローカル / CI）なら何もしない。既に <c>*</c> か同じホストを含む場合も何もしない。
        /// </summary>
        public static void AddWebsiteHostname(HostFilteringOptions options, IConfiguration configuration)
        {
            var websiteHostname = configuration[WebsiteHostnameKey];
            if (string.IsNullOrWhiteSpace(websiteHostname))
            {
                return;
            }

            var allowedHosts = options.AllowedHosts ?? new List<string>();
            if (allowedHosts.Any(h => h == "*" || string.Equals(h, websiteHostname, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // 既定の PostConfigure が AllowedHosts を固定長配列で設定するため、
            // Add ではなく新しいリストに詰め替える。
            options.AllowedHosts = allowedHosts.Append(websiteHostname).ToList();
        }
    }
}
