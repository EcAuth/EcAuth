using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace IdentityProvider.Controllers
{
    [Route("userinfo")]
    [ApiController]
    public class UserinfoController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IUserService _userService;
        private readonly IB2BUserService _b2bUserService;
        private readonly ILogger<UserinfoController> _logger;

        public UserinfoController(
            ITokenService tokenService,
            IUserService userService,
            IB2BUserService b2bUserService,
            ILogger<UserinfoController> logger)
        {
            _tokenService = tokenService;
            _userService = userService;
            _b2bUserService = b2bUserService;
            _logger = logger;
        }

        /// <summary>
        /// OpenID Connect準拠のUserInfo endpoint (GET)
        /// アクセストークンを受け取り、ユーザー情報を返します
        /// </summary>
        /// <param name="authorization">Authorization header (Bearer token)</param>
        /// <returns>ユーザー情報（個人情報保護法準拠）</returns>
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            return await ProcessUserInfoRequest();
        }

        /// <summary>
        /// OpenID Connect準拠のUserInfo endpoint (POST)
        /// アクセストークンを受け取り、ユーザー情報を返します
        /// </summary>
        /// <returns>ユーザー情報（個人情報保護法準拠）</returns>
        [HttpPost]
        public async Task<IActionResult> Post()
        {
            return await ProcessUserInfoRequest();
        }

        /// <summary>
        /// UserInfo リクエスト処理の共通ロジック
        /// </summary>
        /// <returns>ユーザー情報または適切なエラーレスポンス</returns>
        private async Task<IActionResult> ProcessUserInfoRequest()
        {
            try
            {
                _logger.LogInformation("UserInfo endpoint accessed");

                // 1. Authorization ヘッダーの取得
                var authorizationHeader = HttpContext.Request.Headers["Authorization"].FirstOrDefault();
                if (string.IsNullOrEmpty(authorizationHeader))
                {
                    _logger.LogWarning("Authorization header is missing");
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "アクセストークンが提供されていません。"
                    });
                }

                // 2. Bearer Token の解析
                AuthenticationHeaderValue authHeaderValue;
                try
                {
                    authHeaderValue = AuthenticationHeaderValue.Parse(authorizationHeader);
                }
                catch (FormatException)
                {
                    _logger.LogWarning("Invalid Authorization header format: {Header}", authorizationHeader);
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "Authorizationヘッダーの形式が正しくありません。"
                    });
                }

                if (authHeaderValue.Scheme != "Bearer")
                {
                    _logger.LogWarning("Unsupported authentication scheme: {Scheme}", authHeaderValue.Scheme);
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "Bearer認証のみサポートされています。"
                    });
                }

                var accessToken = authHeaderValue.Parameter;
                if (string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogWarning("Access token is empty");
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "アクセストークンが空です。"
                    });
                }

                // 3. アクセストークンの検証（SubjectType を含む詳細な結果を取得）
                _logger.LogInformation("Validating access token");
                var validationResult = await _tokenService.ValidateAccessTokenWithTypeAsync(accessToken);

                if (!validationResult.IsValid || validationResult.Subject == null)
                {
                    _logger.LogWarning("Invalid or expired access token");
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "無効なアクセストークンまたは期限切れです。"
                    });
                }

                var subject = validationResult.Subject;
                var subjectType = validationResult.SubjectType ?? SubjectType.B2C;

                // 4. SubjectType に応じたユーザー情報の取得
                _logger.LogInformation("Fetching user info for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);

                string? userSubject = null;
                switch (subjectType)
                {
                    case SubjectType.B2B:
                        var b2bUser = await _b2bUserService.GetBySubjectAsync(subject);
                        if (b2bUser != null)
                        {
                            userSubject = b2bUser.Subject;
                        }
                        break;

                    case SubjectType.B2C:
                    default:
                        var ecAuthUser = await _userService.GetUserBySubjectAsync(subject);
                        if (ecAuthUser != null)
                        {
                            userSubject = ecAuthUser.Subject;
                        }
                        break;
                }

                if (userSubject == null)
                {
                    _logger.LogError("User not found for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "ユーザーが見つかりません。"
                    });
                }

                // 5. OpenID Connect準拠のレスポンス（個人情報保護法準拠）
                _logger.LogInformation("UserInfo request processed successfully for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);
                return Ok(new { sub = userSubject });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in UserInfo endpoint - Message: {Message}", ex.Message);

                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }
    }
}