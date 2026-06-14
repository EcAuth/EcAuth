using System.Text.Json.Serialization;
using IdentityProvider.Exceptions;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// Account 申込フロー（Phase D-1）の API エンドポイント。
    /// 申込リクエストの受付（確認メール送信）、メール確認による本登録、申込状況の照会を提供する。
    /// </summary>
    [Route("api/signup")]
    [ApiController]
    [EnableCors(SignupController.CorsPolicy)]
    public class SignupController : ControllerBase
    {
        /// <summary>申込 API（accounts サイト）向けの CORS ポリシー名。</summary>
        public const string CorsPolicy = "SignupApiCors";

        private readonly ISignupService _signupService;
        private readonly ILogger<SignupController> _logger;

        public SignupController(ISignupService signupService, ILogger<SignupController> logger)
        {
            _signupService = signupService;
            _logger = logger;
        }

        /// <summary>
        /// 申込リクエストを受け付け、確認メールを送信する。
        /// </summary>
        [HttpPost]
        [Route("request")]
        public async Task<IActionResult> Request([FromBody] SignupRequestDto body, CancellationToken ct)
        {
            if (body == null)
            {
                return UnprocessableEntity(new
                {
                    error = "invalid_request",
                    error_description = "リクエストボディが指定されていません。",
                    field = (string?)null
                });
            }

            try
            {
                var input = new SignupInput
                {
                    Email = body.Email,
                    OrganizationName = body.OrganizationName,
                    ContactName = body.ContactName,
                    ProductionSiteUrl = body.ProductionSiteUrl,
                    TestSiteUrl = body.TestSiteUrl,
                    EcCubeVersion = body.EcCubeVersion,
                    TermsVersion = body.TermsVersion,
                    PrivacyVersion = body.PrivacyVersion,
                    CookieVersion = body.CookieVersion
                };

                await _signupService.RequestAsync(input, ct);

                return Accepted(new
                {
                    message = "確認メールを送信しました。メール内のリンクから申込を完了してください。"
                });
            }
            catch (SignupValidationException ex)
            {
                _logger.LogWarning(
                    "申込リクエストのバリデーションに失敗しました: Error={Error}, Field={Field}",
                    ex.Error, ex.Field);
                return StatusCode(ex.StatusCode, new
                {
                    error = ex.Error,
                    error_description = ex.ErrorDescription,
                    field = ex.Field
                });
            }
        }

        /// <summary>
        /// 確認トークンを検証し、本登録レコード一式を生成する。
        /// </summary>
        [HttpPost]
        [Route("confirm")]
        public async Task<IActionResult> Confirm([FromBody] SignupConfirmDto body, CancellationToken ct)
        {
            try
            {
                var signupRequest = await _signupService.ConfirmAsync(body?.Token ?? string.Empty, ct);

                return Ok(new
                {
                    message = "申込が完了しました。",
                    email = signupRequest.Email
                });
            }
            catch (SignupValidationException ex)
            {
                _logger.LogWarning(
                    "申込確認のバリデーションに失敗しました: Error={Error}, Field={Field}",
                    ex.Error, ex.Field);
                return StatusCode(ex.StatusCode, new
                {
                    error = ex.Error,
                    error_description = ex.ErrorDescription,
                    field = ex.Field
                });
            }
        }

        /// <summary>
        /// 確認トークンに対応する申込の状況を返す。
        /// </summary>
        [HttpGet]
        [Route("status/{token}")]
        public async Task<IActionResult> Status(string token, CancellationToken ct)
        {
            var status = await _signupService.GetStatusAsync(token, ct);

            return Ok(new
            {
                status = ToStatusString(status)
            });
        }

        private static string ToStatusString(SignupStatus status) => status switch
        {
            SignupStatus.Pending => "pending",
            SignupStatus.Confirmed => "confirmed",
            SignupStatus.Expired => "expired",
            _ => "not_found"
        };

        /// <summary>
        /// <c>POST /api/signup/request</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class SignupRequestDto
        {
            [JsonPropertyName("email")]
            public string? Email { get; set; }

            [JsonPropertyName("organization_name")]
            public string? OrganizationName { get; set; }

            [JsonPropertyName("contact_name")]
            public string? ContactName { get; set; }

            [JsonPropertyName("production_site_url")]
            public string? ProductionSiteUrl { get; set; }

            [JsonPropertyName("test_site_url")]
            public string? TestSiteUrl { get; set; }

            [JsonPropertyName("ec_cube_version")]
            public string? EcCubeVersion { get; set; }

            [JsonPropertyName("terms_version")]
            public string? TermsVersion { get; set; }

            [JsonPropertyName("privacy_version")]
            public string? PrivacyVersion { get; set; }

            [JsonPropertyName("cookie_version")]
            public string? CookieVersion { get; set; }
        }

        /// <summary>
        /// <c>POST /api/signup/confirm</c> のリクエストボディ（snake_case）。
        /// </summary>
        public sealed class SignupConfirmDto
        {
            [JsonPropertyName("token")]
            public string? Token { get; set; }
        }
    }
}
