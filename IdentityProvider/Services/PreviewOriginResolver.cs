using System.Text.RegularExpressions;

namespace IdentityProvider.Services
{
    /// <summary>
    /// フロントエンド（ecauth-website）の PR プレビューの配信元を、テナントごとのパターンで許可する
    /// （EcAuthDocs#159）。
    /// <para>
    /// stg-accounts は PR ごとに Cloudflare Pages のプレビュー（<c>https://&lt;hash|branch&gt;.ecauth-website-stg.pages.dev</c>）
    /// から使われるため、固定オリジンの設定（<c>Signup:AllowedOrigins</c> / <c>Signup:ConfirmBaseUrl:{tenant}</c> 等）
    /// だけでは足りない。パターンは <c>PreviewOrigins:{tenant}:{n}</c>（例: 環境変数
    /// <c>PreviewOrigins__stg_accounts__0=https://*.ecauth-website-stg.pages.dev</c>）で与え、
    /// 未設定のテナント（accounts 等）は一切許可しない。
    /// </para>
    /// <para>
    /// パターンは <c>https://*.&lt;suffix&gt;</c> の形だけを受け付け、<c>*</c> は DNS ラベル 1 つにだけ一致させる
    /// （Pages のプレビュー URL はプロジェクト直下の 1 ラベル）。<c>*.pages.dev</c> のように他人のプロジェクトまで
    /// 含んでしまう広いパターンを誤って書かないよう、suffix は 3 ラベル以上を必須とする。
    /// </para>
    /// </summary>
    public interface IPreviewOriginResolver
    {
        /// <summary>
        /// <paramref name="origin"/> が <paramref name="tenantName"/> のプレビューパターンに一致する
        /// オリジン（<c>https://host</c>、パス・クエリ・ポートなし）なら true。
        /// </summary>
        bool IsAllowed(string? tenantName, string? origin);

        /// <summary>
        /// 現在のリクエストの <c>Origin</c> ヘッダが現テナントのプレビューパターンに一致すればそのオリジン、
        /// 一致しなければ null。確認 URL・マジックリンク・課金の戻り先の基底 URL を、固定値の代わりに
        /// プレビューへ向けるために使う。
        /// </summary>
        string? ResolveRequestOrigin();
    }

    /// <inheritdoc />
    public sealed class PreviewOriginResolver : IPreviewOriginResolver
    {
        /// <summary>構成セクション名（<c>PreviewOrigins:{tenant}:{n}</c>）。</summary>
        public const string SectionName = "PreviewOrigins";

        private const string WildcardPrefix = "https://*.";

        // 環境変数名はハイフンを含められないため、キーのテナント部は [A-Za-z0-9_] 以外を "_" にする
        // （SignupService / MagicLinkService / BillingService と同じ規約。"stg-accounts" -> "stg_accounts"）。
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        // ワイルドカードに一致させる DNS ラベル 1 つ（Pages のコミットハッシュ / ブランチエイリアス）。
        private static readonly Regex DnsLabel = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

        private readonly IConfiguration _configuration;
        private readonly ITenantService _tenantService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<PreviewOriginResolver> _logger;

        public PreviewOriginResolver(
            IConfiguration configuration,
            ITenantService tenantService,
            IHttpContextAccessor httpContextAccessor,
            ILogger<PreviewOriginResolver> logger)
        {
            _configuration = configuration;
            _tenantService = tenantService;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool IsAllowed(string? tenantName, string? origin)
        {
            if (string.IsNullOrWhiteSpace(tenantName) || !TryGetHost(origin, out var host))
            {
                return false;
            }

            foreach (var suffix in Suffixes(tenantName))
            {
                if (host.EndsWith("." + suffix, StringComparison.Ordinal)
                    && DnsLabel.IsMatch(host[..^(suffix.Length + 1)]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <inheritdoc />
        public string? ResolveRequestOrigin()
        {
            var origin = _httpContextAccessor.HttpContext?.Request.Headers.Origin.ToString();
            if (string.IsNullOrEmpty(origin) || !IsAllowed(_tenantService.TenantName, origin))
            {
                return null;
            }
            return origin;
        }

        /// <summary>
        /// テナントのパターンから suffix（<c>https://*.</c> を除いた部分、小文字）を列挙する。
        /// 形式に合わないパターンは設定ミスとして警告し、無視する（安全側）。
        /// </summary>
        private IEnumerable<string> Suffixes(string tenantName)
        {
            var key = $"{SectionName}:{NonConfigKeyChar.Replace(tenantName, "_")}";
            var patterns = _configuration.GetSection(key).Get<string[]>() ?? Array.Empty<string>();
            foreach (var raw in patterns)
            {
                var pattern = raw?.Trim().TrimEnd('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                var suffix = pattern.StartsWith(WildcardPrefix, StringComparison.Ordinal)
                    ? pattern[WildcardPrefix.Length..]
                    : null;
                var labels = suffix?.Split('.');
                if (labels == null || labels.Length < 3 || !labels.All(DnsLabel.IsMatch))
                {
                    _logger.LogWarning(
                        "プレビューオリジンのパターンが不正なため無視します: Key={Key}, Pattern={Pattern}", key, raw);
                    continue;
                }
                yield return suffix!;
            }
        }

        /// <summary>
        /// <c>https://host</c>（既定ポート、パス・クエリ・フラグメント・userinfo なし）の形なら host（小文字）を返す。
        /// ブラウザが送る Origin ヘッダと同じ正規形だけを受け付け、末尾スラッシュ等の揺れは不一致とする。
        /// </summary>
        private static bool TryGetHost(string? origin, out string host)
        {
            host = string.Empty;
            if (string.IsNullOrEmpty(origin)
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.HostNameType != UriHostNameType.Dns)
            {
                return false;
            }

            host = uri.IdnHost.ToLowerInvariant();
            return string.Equals(origin, $"https://{host}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
