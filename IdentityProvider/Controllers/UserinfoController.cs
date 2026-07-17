using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Telemetry;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace IdentityProvider.Controllers
{
    [Route("v{version:apiVersion}/userinfo")]
    [ApiController]
    [ApiVersion("1.0")]
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

                // 0. OAuth 2.1 §5.3 / RFC 6750 §3.1: アクセストークンを URI クエリパラメータで
                //    送信することは禁止（サーバーログ・ブラウザ履歴・Referer 経由の漏洩防止）。
                //    ?access_token=... が付いている場合は無視せず明示的に 400 で拒否する。
                if (HttpContext.Request.Query.ContainsKey("access_token"))
                {
                    _logger.LogWarning("Access token supplied via query parameter - rejected (OAuth 2.1 5.3)");
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "アクセストークンを URI クエリパラメータで送信することは許可されていません。Authorization ヘッダーを使用してください。"
                    });
                }

                // 1. Authorization ヘッダーの取得 + Bearer Token 解析
                string accessToken;
                using (TimingScope.Begin("auth_header_parse"))
                {
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

                    AuthenticationHeaderValue authHeaderValue;
                    try
                    {
                        authHeaderValue = AuthenticationHeaderValue.Parse(authorizationHeader);
                    }
                    catch (FormatException)
                    {
                        // Authorization ヘッダーの生値はアクセストークンを含み得るためログに出力しない
                        _logger.LogWarning("Invalid Authorization header format");
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

                    var token = authHeaderValue.Parameter;
                    if (string.IsNullOrEmpty(token))
                    {
                        _logger.LogWarning("Access token is empty");
                        return Unauthorized(new
                        {
                            error = "invalid_token",
                            error_description = "アクセストークンが空です。"
                        });
                    }
                    accessToken = token;
                }

                // 2. アクセストークンの検証（SubjectType を含む詳細な結果を取得）
                _logger.LogInformation("Validating access token");
                ITokenService.AccessTokenValidationResult validationResult;
                using (TimingScope.Begin("access_token_validate"))
                {
                    validationResult = await _tokenService.ValidateAccessTokenWithTypeAsync(accessToken);
                }

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

                // 3. SubjectType に応じたユーザー情報の取得
                _logger.LogInformation("Fetching user info for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);

                string? userSubject = null;
                using (TimingScope.Begin("user_lookup"))
                {
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