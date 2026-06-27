namespace IdentityProvider.Services
{
    /// <summary>
    /// リバースプロキシ（Cloudflare）からの転送ヘッダを信頼するための設定。
    /// <para>
    /// 本番 API ホスト（<c>*.ec-auth.io</c>）は Cloudflare プロキシ配下（<c>proxied = true</c>）のため、
    /// <c>HttpContext.Connection.RemoteIpAddress</c> は Cloudflare のエッジ IP になる。実クライアント IP は
    /// Cloudflare が付与する <c>CF-Connecting-IP</c> から復元する（<c>Program.cs</c> の ForwardedHeaders 設定）。
    /// </para>
    /// <para>
    /// <c>TrustedIpRanges</c> は転送ヘッダを信頼してよい接続元（= Cloudflare のエッジ）の CIDR。
    /// これを既知ネットワークとして登録し、Cloudflare 以外から来た <c>CF-Connecting-IP</c> は信頼しない
    /// （IP 偽装によるレート制限回避・標的型 DoS を防ぐ）。ただし最終的な信頼境界は本番 App Service の
    /// オリジンロック（Azure access restriction = Cloudflare IP 限定）で担保する。
    /// </para>
    /// <para>
    /// 既定値は Cloudflare 公開リスト（<c>https://www.cloudflare.com/ips-v4/</c> /
    /// <c>https://www.cloudflare.com/ips-v6/</c>）。Cloudflare がレンジを変更した場合は、本番 infra の
    /// access restriction と<strong>合わせて</strong>更新すること（信頼境界の出典を一致させる）。
    /// 構成セクション <c>Cloudflare:TrustedIpRanges</c> で上書き可能。
    /// </para>
    /// </summary>
    public sealed class CloudflareOptions
    {
        public const string SectionName = "Cloudflare";

        /// <summary>
        /// 転送ヘッダを信頼する Cloudflare エッジの CIDR レンジ（IPv4 / IPv6）。
        /// 出典: https://www.cloudflare.com/ips-v4/ , https://www.cloudflare.com/ips-v6/
        /// </summary>
        public List<string> TrustedIpRanges { get; set; } = new()
        {
            // IPv4
            "173.245.48.0/20",
            "103.21.244.0/22",
            "103.22.200.0/22",
            "103.31.4.0/22",
            "141.101.64.0/18",
            "108.162.192.0/18",
            "190.93.240.0/20",
            "188.114.96.0/20",
            "197.234.240.0/22",
            "198.41.128.0/17",
            "162.158.0.0/15",
            "104.16.0.0/13",
            "104.24.0.0/14",
            "172.64.0.0/13",
            "131.0.72.0/22",
            // IPv6
            "2400:cb00::/32",
            "2606:4700::/32",
            "2803:f800::/32",
            "2405:b500::/32",
            "2405:8100::/32",
            "2a06:98c0::/29",
            "2c0f:f248::/32",
        };
    }
}
