using System.Text.Json.Serialization;
using IdentityProvider.Exceptions;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// マジックリンクログイン（パスキー紛失時のリカバリ動線）の API エンドポイント。
    /// 申込フローと同じくフロントエンド（Cloudflare Pages）から呼ばれるクロスオリジン API のため、
    /// <see cref="SignupController.CorsPolicy"/> を共用する。
    /// <para>
    /// メールリンク（<c>/signin/magic-link?token=...</c>）はフロントエンドのページを指し、
    /// ユーザー操作で <c>POST /verify</c> を発行する（D-1 確認ページと同方針）。これにより、
    /// メールセキュリティスキャナの先読み（プリフェッチ）で状態変更 GET が誤消費される問題を回避する。
    /// </para>
    /// </summary>
    [Route("api/account/magic-link")]
    [ApiController]
    [EnableCors(SignupController.CorsPolicy)]
    public class MagicLinkController : ControllerBase
    {
        private readonly IMagicLinkService _magicLinkService;
        private readonly ILogger<MagicLinkController> _logger;

        public MagicLinkController(IMagicLinkService magicLinkService, ILogger<MagicLinkController> logger)
        {
            _magicLinkService = magicLinkService;
            _logger = logger;
        }

        /// <summary>
        /// マジックリンクの発行を要求する。Email enumeration 対策のため、Account の存在有無に関わらず
        /// 常に同一の HTTP 200 + 同一メッセージを返す。レート制限超過時のみ 429 を返す。
        /// </summary>
        [HttpPost]
        [Route("request")]
        public async Task<IActionResult> RequestMagicLink([FromBody] MagicLinkRequestDto? body, CancellationToken ct)
        {
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = HttpContext.Request.Headers.UserAgent.ToString();

            try
            {
                await _magicLinkService.RequestAsync(body?.Email, ipAddress, userAgent, ct);
            }
            catch (MagicLinkException ex)
            {
                // レート制限（429）。閾値は Account 存在有無に依存しないため enumeration は漏れない。
                _logger.LogWarning("マジックリンク要求が拒否されました: Error={Error}", ex.Error);
                return StatusCode(ex.StatusCode, new
                {
                    error = ex.Error,
                    error_description = ex.ErrorDescription
                });
            }

            // Account の存在有無を漏らさないため常に同一メッセージを返す。
            return Ok(new
            {
                message = "メールアドレスが登録されている場合、ログインリンクをお送りしました。"
            });
        }

        /// <summary>
        /// マジックリンクのトークンを検証して単発消費し、認可コードを付与したリダイレクト先を返す。
        /// フロントエンドはレスポンスの <c>location</c> へブラウザ遷移する。
        /// </summary>
        [HttpPost]
        [Route("verify")]
        public async Task<IActionResult> Verify([FromBody] MagicLinkVerifyDto? body, CancellationToken ct)
        {
            try
            {
                var result = await _magicLinkService.VerifyAsync(body?.Token ?? string.Empty, ct);
                return Ok(new
                {
                    location = result.RedirectUri
                });
            }
            catch (MagicLinkException ex)
            {
                _logger.LogWarning("マジックリンク検証に失敗しました: Error={Error}", ex.Error);
                return StatusCode(ex.StatusCode, new
                {
                    error = ex.Error,
                    error_description = ex.ErrorDescription
                });
            }
        }

        /// <summary>
        /// <c>POST /api/account/magic-link/request</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class MagicLinkRequestDto
        {
            [JsonPropertyName("email")]
            public string? Email { get; set; }
        }

        /// <summary>
        /// <c>POST /api/account/magic-link/verify</c> のリクエストボディ（snake_case）。
        /// トークンを URL に載せないためボディで受け取る（Azure Monitor の requests.url 露出を避ける）。
        /// </summary>
        public sealed class MagicLinkVerifyDto
        {
            [JsonPropertyName("token")]
            public string? Token { get; set; }
        }
    }
}
