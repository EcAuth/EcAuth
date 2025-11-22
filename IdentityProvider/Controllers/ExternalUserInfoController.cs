using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IdentityProvider.Controllers
{
    [Route("api/external-userinfo")]
    [ApiController]
    public class ExternalUserInfoController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IExternalUserInfoService _externalUserInfoService;
        private readonly ILogger<ExternalUserInfoController> _logger;

        public ExternalUserInfoController(
            ITokenService tokenService,
            IExternalUserInfoService externalUserInfoService,
            ILogger<ExternalUserInfoController> logger)
        {
            _tokenService = tokenService;
            _externalUserInfoService = externalUserInfoService;
            _logger = logger;
        }

        /// <summary>
        /// 外部IdPのユーザー情報を透過的に取得するAPI
        /// EcAuthのアクセストークンとprovider nameを指定して、外部IdPのユーザー詳細情報を取得
        /// </summary>
        /// <param name="provider">外部IdPのprovider name（例: google-oauth2, federate-oauth2）</param>
        /// <returns>外部IdPから取得したユーザー情報（JSON）</returns>
        [HttpGet]
        public async Task<IActionResult> GetExternalUserInfo([FromQuery] string? provider)
        {
            try
            {
                _logger.LogInformation("External UserInfo endpoint accessed with provider: {Provider}", provider ?? "null");

                // 1. providerパラメータのバリデーション
                if (string.IsNullOrEmpty(provider))
                {
                    _logger.LogWarning("Provider parameter is missing");
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "providerパラメータが必要です。"
                    });
                }

                // 2. Authorization ヘッダーの取得
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

                // 3. Bearer Token の解析
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

                // 4. アクセストークンの検証
                _logger.LogInformation("Validating access token");
                var subject = await _tokenService.ValidateAccessTokenAsync(accessToken);

                if (subject == null)
                {
                    _logger.LogWarning("Invalid or expired access token");
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "無効なアクセストークンまたは期限切れです。"
                    });
                }

                // 5. 外部IdPユーザー情報の取得
                _logger.LogInformation("Fetching external user info for subject {Subject} and provider {Provider}",
                    subject, provider);

                var externalUserInfo = await _externalUserInfoService.GetExternalUserInfoAsync(
                    new IExternalUserInfoService.GetExternalUserInfoRequest
                    {
                        EcAuthSubject = subject,
                        ExternalProvider = provider
                    });

                if (externalUserInfo == null)
                {
                    _logger.LogWarning("External user info not found for subject {Subject} and provider {Provider}",
                        subject, provider);
                    return NotFound(new
                    {
                        error = "not_found",
                        error_description = "指定されたproviderのユーザー情報が見つかりません。"
                    });
                }

                // 6. レスポンスの構築（外部IdPから取得した情報にproviderフィールドを追加）
                _logger.LogInformation("External user info retrieved successfully for subject {Subject} and provider {Provider}",
                    subject, provider);

                // JsonDocumentをDictionaryに変換してproviderフィールドを追加
                var userInfoDict = new Dictionary<string, object>();
                foreach (var property in externalUserInfo.UserInfoClaims.RootElement.EnumerateObject())
                {
                    userInfoDict[property.Name] = ConvertJsonElement(property.Value);
                }
                userInfoDict["provider"] = externalUserInfo.ExternalProvider;

                return Ok(userInfoDict);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in External UserInfo endpoint - Message: {Message}", ex.Message);

                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        /// <summary>
        /// JsonElementを適切なC#オブジェクトに変換するヘルパーメソッド
        /// </summary>
        private static object ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
                JsonValueKind.Null => null!,
                _ => element.ToString()
            };
        }
    }
}
