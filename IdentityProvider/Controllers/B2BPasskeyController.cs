using Fido2NetLib;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Web;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// B2Bパスキー認証APIコントローラー
    /// </summary>
    [Route("b2b/passkey")]
    [ApiController]
    public class B2BPasskeyController : ControllerBase
    {
        private readonly IB2BPasskeyService _passkeyService;
        private readonly IAuthorizationCodeService _authorizationCodeService;
        private readonly ITokenService _tokenService;
        private readonly IB2BUserService _b2bUserService;
        private readonly EcAuthDbContext _context;
        private readonly ILogger<B2BPasskeyController> _logger;

        public B2BPasskeyController(
            IB2BPasskeyService passkeyService,
            IAuthorizationCodeService authorizationCodeService,
            ITokenService tokenService,
            IB2BUserService b2bUserService,
            EcAuthDbContext context,
            ILogger<B2BPasskeyController> logger)
        {
            _passkeyService = passkeyService;
            _authorizationCodeService = authorizationCodeService;
            _tokenService = tokenService;
            _b2bUserService = b2bUserService;
            _context = context;
            _logger = logger;
        }

        #region Request/Response DTOs

        /// <summary>
        /// 登録オプション生成リクエスト
        /// </summary>
        public class RegisterOptionsRequest
        {
            [JsonPropertyName("client_id")]
            public string ClientId { get; set; } = string.Empty;
            [JsonPropertyName("client_secret")]
            public string ClientSecret { get; set; } = string.Empty;
            [JsonPropertyName("rp_id")]
            public string RpId { get; set; } = string.Empty;
            [JsonPropertyName("b2b_subject")]
            public string B2BSubject { get; set; } = string.Empty;
            [JsonPropertyName("display_name")]
            public string? DisplayName { get; set; }
            [JsonPropertyName("device_name")]
            public string? DeviceName { get; set; }
            [JsonPropertyName("external_id")]
            public string ExternalId { get; set; } = string.Empty;
        }

        /// <summary>
        /// 登録検証リクエスト
        /// </summary>
        public class RegisterVerifyRequest
        {
            [JsonPropertyName("client_id")]
            public string ClientId { get; set; } = string.Empty;
            [JsonPropertyName("client_secret")]
            public string ClientSecret { get; set; } = string.Empty;
            [JsonPropertyName("session_id")]
            public string SessionId { get; set; } = string.Empty;
            [JsonPropertyName("response")]
            public AuthenticatorAttestationRawResponse Response { get; set; } = null!;
            [JsonPropertyName("device_name")]
            public string? DeviceName { get; set; }
        }

        /// <summary>
        /// 認証オプション生成リクエスト
        /// </summary>
        public class AuthenticateOptionsRequest
        {
            [JsonPropertyName("client_id")]
            public string ClientId { get; set; } = string.Empty;
            [JsonPropertyName("rp_id")]
            public string RpId { get; set; } = string.Empty;
            [JsonPropertyName("b2b_subject")]
            public string? B2BSubject { get; set; }
        }

        /// <summary>
        /// 認証検証リクエスト
        /// </summary>
        public class AuthenticateVerifyRequest
        {
            [JsonPropertyName("client_id")]
            public string ClientId { get; set; } = string.Empty;
            [JsonPropertyName("session_id")]
            public string SessionId { get; set; } = string.Empty;
            [JsonPropertyName("redirect_uri")]
            public string RedirectUri { get; set; } = string.Empty;
            [JsonPropertyName("state")]
            public string? State { get; set; }
            [JsonPropertyName("response")]
            public AuthenticatorAssertionRawResponse Response { get; set; } = null!;
        }

        #endregion

        #region Registration Endpoints

        /// <summary>
        /// パスキー登録オプション生成
        /// POST /b2b/passkey/register/options
        /// </summary>
        [HttpPost("register/options")]
        public async Task<IActionResult> RegisterOptions([FromBody] RegisterOptionsRequest request)
        {
            try
            {
                _logger.LogInformation("B2B Passkey RegisterOptions requested for client: {ClientId}", request.ClientId);

                // バリデーション
                if (string.IsNullOrWhiteSpace(request.ClientId))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "client_id は必須です。"
                    });
                }

                if (string.IsNullOrWhiteSpace(request.B2BSubject))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "b2b_subject は必須です。"
                    });
                }

                if (string.IsNullOrWhiteSpace(request.ExternalId))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "external_id は必須です。"
                    });
                }

                // クライアント認証
                var client = await AuthenticateClientAsync(request.ClientId, request.ClientSecret);
                if (client == null)
                {
                    _logger.LogWarning("Client authentication failed for: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_client",
                        error_description = "クライアント認証に失敗しました。"
                    });
                }

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.RegistrationOptionsRequest
                {
                    ClientId = request.ClientId,
                    RpId = request.RpId,
                    B2BSubject = request.B2BSubject,
                    DisplayName = request.DisplayName,
                    DeviceName = request.DeviceName,
                    ExternalId = request.ExternalId
                };

                var result = await _passkeyService.CreateRegistrationOptionsAsync(serviceRequest);

                _logger.LogInformation("B2B Passkey RegisterOptions generated successfully. SessionId: {SessionId}", result.SessionId);

                return Ok(new
                {
                    session_id = result.SessionId,
                    options = result.Options,
                    is_provisioned = result.IsProvisioned
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Validation error in RegisterOptions: {Message}", ex.Message);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = ex.Message
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Operation error in RegisterOptions: {Message}", ex.Message);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RegisterOptions: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        /// <summary>
        /// パスキー登録検証
        /// POST /b2b/passkey/register/verify
        /// </summary>
        [HttpPost("register/verify")]
        public async Task<IActionResult> RegisterVerify([FromBody] RegisterVerifyRequest request)
        {
            try
            {
                _logger.LogInformation("B2B Passkey RegisterVerify requested. SessionId: {SessionId}", request.SessionId);

                // バリデーション
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "session_id は必須です。"
                    });
                }

                // クライアント認証
                var client = await AuthenticateClientAsync(request.ClientId, request.ClientSecret);
                if (client == null)
                {
                    _logger.LogWarning("Client authentication failed for: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_client",
                        error_description = "クライアント認証に失敗しました。"
                    });
                }

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.RegistrationVerifyRequest
                {
                    SessionId = request.SessionId,
                    ClientId = request.ClientId,
                    AttestationResponse = request.Response,
                    DeviceName = request.DeviceName
                };

                var result = await _passkeyService.VerifyRegistrationAsync(serviceRequest);

                if (!result.Success)
                {
                    _logger.LogWarning("B2B Passkey registration verification failed: {ErrorMessage}", result.ErrorMessage);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = result.ErrorMessage ?? "パスキー登録の検証に失敗しました。"
                    });
                }

                _logger.LogInformation("B2B Passkey registered successfully. CredentialId: {CredentialId}", result.CredentialId);

                return Ok(new
                {
                    success = true,
                    credential_id = result.CredentialId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RegisterVerify: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        #endregion

        #region Authentication Endpoints

        /// <summary>
        /// パスキー認証オプション生成
        /// POST /b2b/passkey/authenticate/options
        /// </summary>
        [HttpPost("authenticate/options")]
        public async Task<IActionResult> AuthenticateOptions([FromBody] AuthenticateOptionsRequest request)
        {
            try
            {
                _logger.LogInformation("B2B Passkey AuthenticateOptions requested for client: {ClientId}", request.ClientId);

                // バリデーション
                if (string.IsNullOrWhiteSpace(request.ClientId))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "client_id は必須です。"
                    });
                }

                // クライアント存在確認（認証なし、client_idのみ）
                var client = await _context.Clients
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

                if (client == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_client",
                        error_description = "クライアントが見つかりません。"
                    });
                }

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.AuthenticationOptionsRequest
                {
                    ClientId = request.ClientId,
                    RpId = request.RpId,
                    B2BSubject = request.B2BSubject
                };

                var result = await _passkeyService.CreateAuthenticationOptionsAsync(serviceRequest);

                _logger.LogInformation("B2B Passkey AuthenticateOptions generated successfully. SessionId: {SessionId}", result.SessionId);

                return Ok(new
                {
                    session_id = result.SessionId,
                    options = result.Options
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Validation error in AuthenticateOptions: {Message}", ex.Message);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = ex.Message
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Operation error in AuthenticateOptions: {Message}", ex.Message);
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AuthenticateOptions: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        /// <summary>
        /// パスキー認証検証・認可コード発行
        /// POST /b2b/passkey/authenticate/verify
        /// </summary>
        [HttpPost("authenticate/verify")]
        public async Task<IActionResult> AuthenticateVerify([FromBody] AuthenticateVerifyRequest request)
        {
            try
            {
                _logger.LogInformation("B2B Passkey AuthenticateVerify requested. SessionId: {SessionId}", request.SessionId);

                // バリデーション
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "session_id は必須です。"
                    });
                }

                if (string.IsNullOrWhiteSpace(request.RedirectUri))
                {
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "redirect_uri は必須です。"
                    });
                }

                // クライアント存在確認・redirect_uri検証
                var client = await _context.Clients
                    .IgnoreQueryFilters()
                    .Include(c => c.RedirectUris)
                    .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);

                if (client == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_client",
                        error_description = "クライアントが見つかりません。"
                    });
                }

                // redirect_uri 検証
                var allowedRedirectUris = client.RedirectUris?
                    .Select(r => r.Uri)
                    .ToList() ?? new List<string>();

                if (!allowedRedirectUris.Contains(request.RedirectUri))
                {
                    _logger.LogWarning("Invalid redirect_uri: {RedirectUri}", request.RedirectUri);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "redirect_uri が許可されていません。"
                    });
                }

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.AuthenticationVerifyRequest
                {
                    SessionId = request.SessionId,
                    ClientId = request.ClientId,
                    AssertionResponse = request.Response
                };

                var result = await _passkeyService.VerifyAuthenticationAsync(serviceRequest);

                if (!result.Success)
                {
                    _logger.LogWarning("B2B Passkey authentication verification failed: {ErrorMessage}", result.ErrorMessage);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = result.ErrorMessage ?? "パスキー認証の検証に失敗しました。"
                    });
                }

                // 認可コード生成
                var authCodeRequest = new IAuthorizationCodeService.AuthorizationCodeRequest
                {
                    Subject = result.B2BSubject!,
                    ClientId = client.Id,
                    RedirectUri = request.RedirectUri,
                    State = request.State,
                    Scope = "openid b2b",
                    ExpirationMinutes = 10,
                    IsB2B = true  // B2B認証フラグ
                };

                var authCode = await _authorizationCodeService.GenerateAuthorizationCodeAsync(authCodeRequest);

                // リダイレクトURL生成
                var redirectUrl = $"{request.RedirectUri}?code={HttpUtility.UrlEncode(authCode.Code)}";
                if (!string.IsNullOrEmpty(request.State))
                {
                    redirectUrl += $"&state={HttpUtility.UrlEncode(request.State)}";
                }

                _logger.LogInformation("B2B Passkey authentication successful. B2BSubject: {B2BSubject}, AuthCode issued", result.B2BSubject);

                return Ok(new
                {
                    redirect_url = redirectUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AuthenticateVerify: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        #endregion

        #region Management Endpoints

        /// <summary>
        /// パスキー一覧取得
        /// GET /b2b/passkey/list
        /// </summary>
        [HttpGet("list")]
        public async Task<IActionResult> List()
        {
            try
            {
                _logger.LogInformation("B2B Passkey List requested");

                // Bearer Token認証
                var subject = await ValidateBearerTokenAsync();
                if (subject == null)
                {
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "無効なアクセストークンまたは期限切れです。"
                    });
                }

                // パスキー一覧取得
                var passkeys = await _passkeyService.GetCredentialsBySubjectAsync(subject);

                _logger.LogInformation("B2B Passkey List returned {Count} passkeys for subject: {Subject}", passkeys.Count, subject);

                return Ok(new
                {
                    passkeys = passkeys.Select(p => new
                    {
                        credential_id = p.CredentialId,
                        device_name = p.DeviceName,
                        aa_guid = p.AaGuid,
                        transports = p.Transports,
                        created_at = p.CreatedAt,
                        last_used_at = p.LastUsedAt
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in List: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        /// <summary>
        /// パスキー削除
        /// DELETE /b2b/passkey/{credentialId}
        /// </summary>
        [HttpDelete("{credentialId}")]
        public async Task<IActionResult> Delete(string credentialId)
        {
            try
            {
                _logger.LogInformation("B2B Passkey Delete requested. CredentialId: {CredentialId}", credentialId);

                // Bearer Token認証
                var subject = await ValidateBearerTokenAsync();
                if (subject == null)
                {
                    return Unauthorized(new
                    {
                        error = "invalid_token",
                        error_description = "無効なアクセストークンまたは期限切れです。"
                    });
                }

                // パスキー削除
                var deleted = await _passkeyService.DeleteCredentialAsync(subject, credentialId);

                if (!deleted)
                {
                    _logger.LogWarning("B2B Passkey not found for deletion. CredentialId: {CredentialId}, Subject: {Subject}", credentialId, subject);
                    return NotFound(new
                    {
                        error = "not_found",
                        error_description = "パスキーが見つかりません。"
                    });
                }

                _logger.LogInformation("B2B Passkey deleted successfully. CredentialId: {CredentialId}", credentialId);

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Delete: {Message}", ex.Message);
                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// クライアント認証（client_id + client_secret）
        /// </summary>
        /// <remarks>
        /// セキュリティ対策: タイミング攻撃防止のため、client_secret の比較には
        /// CryptographicOperations.FixedTimeEquals を使用しています。
        ///
        /// DBクエリ内での文字列比較（== 演算子）は、最初の不一致文字で比較が終了するため、
        /// 応答時間の微妙な違いから秘密情報を推測される可能性があります（タイミング攻撃）。
        /// FixedTimeEquals は入力の長さに関係なく一定時間で比較を行うため、
        /// このリスクを軽減できます。
        ///
        /// 参考: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals
        /// </remarks>
        private async Task<Client?> AuthenticateClientAsync(string clientId, string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                return null;
            }

            // client_id のみでクエリし、client_secret はアプリケーション側で比較
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.ClientId == clientId);

            if (client?.ClientSecret == null)
            {
                return null;
            }

            // タイミング攻撃対策: 定時間比較
            var secretBytes = System.Text.Encoding.UTF8.GetBytes(clientSecret);
            var storedSecretBytes = System.Text.Encoding.UTF8.GetBytes(client.ClientSecret);

            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(secretBytes, storedSecretBytes))
            {
                return client;
            }

            return null;
        }

        /// <summary>
        /// Bearer Token認証
        /// </summary>
        private async Task<string?> ValidateBearerTokenAsync()
        {
            var authorizationHeader = HttpContext.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                _logger.LogWarning("Authorization header is missing");
                return null;
            }

            AuthenticationHeaderValue authHeaderValue;
            try
            {
                authHeaderValue = AuthenticationHeaderValue.Parse(authorizationHeader);
            }
            catch (FormatException)
            {
                _logger.LogWarning("Invalid Authorization header format");
                return null;
            }

            if (authHeaderValue.Scheme != "Bearer")
            {
                _logger.LogWarning("Unsupported authentication scheme: {Scheme}", authHeaderValue.Scheme);
                return null;
            }

            var accessToken = authHeaderValue.Parameter;
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Access token is empty");
                return null;
            }

            var subject = await _tokenService.ValidateAccessTokenAsync(accessToken);
            return subject;
        }

        #endregion
    }
}
