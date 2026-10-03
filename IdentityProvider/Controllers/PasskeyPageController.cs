using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// accounts オリジン（RP ID=accounts.ec-auth.io）で表示するパスキー認証 UI。
    /// マイページ（ec-auth.io）は OAuth2(PKCE) の認可リクエストとして本ページに遷移し、
    /// ここで navigator.credentials によるパスキー認証を行い、認可コードを
    /// redirect_uri（ec-auth.io/auth/callback）へ返す。
    ///
    /// 静的ファイル（wwwroot）は本番でサブドメイン制約があるため、Razor ビュー +
    /// コントローラルートで配信する（AuthorizationCallbackController と同じ流儀）。
    /// バージョンプレフィックスは付けず、フロントの apiBaseUrl 直下（/passkey/...）に置く。
    /// </summary>
    [Route("passkey")]
    public class PasskeyPageController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ITenantService _tenantService;
        private readonly IPreviewOriginResolver _previewOrigins;

        public PasskeyPageController(
            IConfiguration configuration,
            ITenantService tenantService,
            IPreviewOriginResolver previewOrigins)
        {
            _configuration = configuration;
            _tenantService = tenantService;
            _previewOrigins = previewOrigins;
        }

        /// <summary>
        /// フロント（申込〜マイページ）の配信元 base URL。環境ごとに切り替える
        /// （本番 ec-auth.io / staging プレビュー / ローカル）。ビューの遷移先に使う。
        /// <para>
        /// PR プレビュー（EcAuthDocs#159）から遷移してきた場合、フロントはクエリ <c>frontend_origin</c> に
        /// 自身のオリジンを載せる。テナントで許可されたプレビューのパターンに一致するときだけそれを使い、
        /// 未指定・不一致なら設定値に戻す（任意の URL へ誘導させない）。
        /// </para>
        /// </summary>
        private string FrontendBaseUrl(string? frontendOrigin) =>
            _previewOrigins.IsAllowed(_tenantService.TenantName, frontendOrigin)
                ? frontendOrigin!
                : (_configuration["Frontend:BaseUrl"] ?? "https://ec-auth.io").TrimEnd('/');

        /// <summary>
        /// GET /passkey/authenticate
        /// クエリの client_id / redirect_uri / code_challenge / code_challenge_method / state を
        /// JS が読み取り、パスキー認証 → authenticate/verify → 認可コードで redirect_uri へ遷移する。
        /// </summary>
        [HttpGet("authenticate")]
        public IActionResult Authenticate([FromQuery(Name = "frontend_origin")] string? frontendOrigin)
        {
            ViewData["FrontendBaseUrl"] = FrontendBaseUrl(frontendOrigin);
            return View("Authenticate");
        }

        /// <summary>
        /// GET /passkey/register
        /// 申込確認（confirm）直後の初回パスキー登録。登録トークンは URL フラグメント（#token=）で
        /// 受け取り（サーバへ送信されずアクセスログ/Referer に残さない）、client_id はクエリで受ける。
        /// register/options → navigator.credentials.create → register/verify（registration_token で認可）
        /// を行い、成功後マイページへ誘導する。
        /// </summary>
        [HttpGet("register")]
        public IActionResult Register([FromQuery(Name = "frontend_origin")] string? frontendOrigin)
        {
            ViewData["FrontendBaseUrl"] = FrontendBaseUrl(frontendOrigin);
            return View("Register");
        }
    }
}
