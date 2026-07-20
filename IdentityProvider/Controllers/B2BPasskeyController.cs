using Fido2NetLib;
using IdentityProvider.Exceptions;
using IdentityProvider.Filters;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Telemetry;
using IdpUtilities.Security;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Web;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// B2Bパスキー認証APIコントローラー
    /// </summary>
    [Route("v{version:apiVersion}/b2b/passkey")]
    [ApiController]
    [ApiVersion("1.0")]
    // authenticate/verify が認可コードを含む redirect_url を返すため、キャッシュさせない。
    [NoStore]
    public class B2BPasskeyController : ControllerBase
    {
        private readonly IB2BPasskeyService _passkeyService;
        private readonly IAuthorizationCodeService _authorizationCodeService;
        private readonly ITokenService _tokenService;
        private readonly IB2BUserService _b2bUserService;
        private readonly EcAuthDbContext _context;
        private readonly ILogger<B2BPasskeyController> _logger;
        private readonly ISecretProtector _secretProtector;
        private readonly IPasskeyRegistrationTokenService _registrationTokenService;
        private readonly IConfiguration _configuration;

        public B2BPasskeyController(
            IB2BPasskeyService passkeyService,
            IAuthorizationCodeService authorizationCodeService,
            ITokenService tokenService,
            IB2BUserService b2bUserService,
            EcAuthDbContext context,
            ILogger<B2BPasskeyController> logger,
            ISecretProtector secretProtector,
            IPasskeyRegistrationTokenService registrationTokenService,
            IConfiguration configuration)
        {
            _configuration = configuration;
            _passkeyService = passkeyService;
            _authorizationCodeService = authorizationCodeService;
            _tokenService = tokenService;
            _b2bUserService = b2bUserService;
            _context = context;
            _logger = logger;
            _secretProtector = secretProtector;
            _registrationTokenService = registrationTokenService;
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
            /// <summary>
            /// 初回パスキー登録トークン（accounts の public client 経路）。指定時は client_secret の
            /// 代わりにこれで認可し、b2b_subject / external_id はトークンから確定する。
            /// </summary>
            [JsonPropertyName("registration_token")]
            public string? RegistrationToken { get; set; }
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
            /// <summary>初回パスキー登録トークン（register/options と同じ値）。指定時は client_secret 不要。</summary>
            [JsonPropertyName("registration_token")]
            public string? RegistrationToken { get; set; }
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
            /// <summary>
            /// PKCE (RFC 7636) の code_challenge（オプション）。マイページ等の public client が
            /// 指定する。認可コードに束縛し、/v1/token で code_verifier と突き合わせる。
            /// </summary>
            [JsonPropertyName("code_challenge")]
            public string? CodeChallenge { get; set; }
            /// <summary>
            /// PKCE の code_challenge_method（"S256" のみ）。
            /// </summary>
            [JsonPropertyName("code_challenge_method")]
            public string? CodeChallengeMethod { get; set; }
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

                // 認可: registration_token 経路（accounts の初回登録・public client）と
                // 従来の client_secret 経路（EC-CUBE プラグイン等）で分岐する。
                Client client;
                string b2bSubject;
                string externalId;
                if (!string.IsNullOrWhiteSpace(request.RegistrationToken))
                {
                    var authz = await AuthorizeByRegistrationTokenAsync(request.ClientId, request.RegistrationToken);
                    if (authz == null)
                    {
                        _logger.LogWarning("Registration token authorization failed for client: {ClientId}", request.ClientId);
                        return Unauthorized(new
                        {
                            error = "invalid_grant",
                            error_description = "登録トークンが無効か期限切れです。"
                        });
                    }
                    // b2b_subject / external_id はトークンから確定する（リクエスト値は使わない）。
                    client = authz.Value.Client;
                    b2bSubject = authz.Value.Subject;
                    externalId = authz.Value.ExternalId;
                }
                else
                {
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

                    // クライアント認証（client_secret）
                    var authenticated = await AuthenticateClientAsync(request.ClientId, request.ClientSecret);
                    if (authenticated == null)
                    {
                        _logger.LogWarning("Client authentication failed for: {ClientId}", request.ClientId);
                        return Unauthorized(new
                        {
                            error = "invalid_client",
                            error_description = "クライアント認証に失敗しました。"
                        });
                    }
                    client = authenticated;
                    b2bSubject = request.B2BSubject;
                    externalId = request.ExternalId;
                }

                Activity.Current?.SetTag("client.id", client.ClientId);

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.RegistrationOptionsRequest
                {
                    ClientId = request.ClientId,
                    RpId = request.RpId,
                    B2BSubject = b2bSubject,
                    DisplayName = request.DisplayName,
                    DeviceName = request.DeviceName,
                    ExternalId = externalId
                };

                var result = await _passkeyService.CreateRegistrationOptionsAsync(serviceRequest);

                // 登録トークン経路では、発行した session_id をトークンへ束縛する。
                // verify はこの session_id でのみ受理されるため、1 つのトークンから
                // 複数のセッションを並行させて複数クレデンシャルを登録することはできない。
                if (!string.IsNullOrWhiteSpace(request.RegistrationToken)
                    && !await _registrationTokenService.BindSessionAsync(request.RegistrationToken, result.SessionId))
                {
                    _logger.LogWarning("Failed to bind session to registration token for client: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_grant",
                        error_description = "登録トークンが無効か期限切れです。"
                    });
                }

                _logger.LogInformation("B2B Passkey RegisterOptions generated successfully. SessionId: {SessionId}", result.SessionId);

                return Ok(new
                {
                    session_id = result.SessionId,
                    options = result.Options,
                    is_provisioned = result.IsProvisioned,
                    resolved_subject = result.ResolvedSubject,
                    subject_resolution = result.SubjectResolution
                });
            }
            catch (ExternalIdConflictException ex)
            {
                // ex.Message には external_id 値が含まれるためサーバーログには残すが、
                // レスポンスボディに返すとマルチテナント環境で external_id 列挙に悪用され得るので固定文言化する
                _logger.LogWarning("ExternalId conflict in RegisterOptions: {Message}", ex.Message);
                return Conflict(new
                {
                    error = "external_id_conflict",
                    error_description = "The requested external_id is already associated with another user in this organization."
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

                // 認可: registration_token 経路（public client）と client_secret 経路で分岐する。
                Client? client;
                // 登録トークン経路では、トークンが指す Subject を検証対象セッションと突き合わせる。
                string? expectedSubject = null;
                var useRegistrationToken = !string.IsNullOrWhiteSpace(request.RegistrationToken);
                using (TimingScope.Begin("client_authenticate"))
                {
                    if (useRegistrationToken)
                    {
                        var authz = await AuthorizeByRegistrationTokenAsync(request.ClientId, request.RegistrationToken!);
                        client = authz?.Client;

                        // session_id はトークンに束縛済みのものと一致しなければならない。
                        // 一致しない = そのトークンで最後に開始したセッション以外での登録試行。
                        if (client != null && !string.Equals(authz!.Value.BoundSessionId, request.SessionId, StringComparison.Ordinal))
                        {
                            _logger.LogWarning(
                                "Registration token is not bound to the presented session. ClientId={ClientId}",
                                request.ClientId);
                            client = null;
                        }
                        else
                        {
                            expectedSubject = authz?.Subject;
                        }
                    }
                    else
                    {
                        client = await AuthenticateClientAsync(request.ClientId, request.ClientSecret);
                    }
                }
                if (client == null)
                {
                    _logger.LogWarning("Client authentication failed for: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = useRegistrationToken ? "invalid_grant" : "invalid_client",
                        error_description = useRegistrationToken ? "登録トークンが無効か期限切れです。" : "クライアント認証に失敗しました。"
                    });
                }

                Activity.Current?.SetTag("client.id", client.ClientId);

                // 登録トークンは「ゲート」として、クレデンシャルを永続化する前に消費する。
                // 検証成功後に消費すると、消費に失敗（＝別リクエストが先に消費）してもクレデンシャルは
                // 既に保存済みとなり、1 トークンから複数のクレデンシャルが登録され得るため。
                // 副作用として WebAuthn 検証がサーバー側で失敗した場合もトークンは失効する。
                // その場合は申込確認メールから再度リンクを取得してもらう（再取得経路は #460 で整備）。
                if (useRegistrationToken
                    && !await _registrationTokenService.ConsumeAsync(request.RegistrationToken!, request.SessionId))
                {
                    _logger.LogWarning("Registration token could not be consumed for client: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_grant",
                        error_description = "登録トークンが無効か期限切れです。お手数ですが、もう一度お手続きをやり直してください。"
                    });
                }

                // サービス呼び出し
                var serviceRequest = new IB2BPasskeyService.RegistrationVerifyRequest
                {
                    SessionId = request.SessionId,
                    ClientId = request.ClientId,
                    AttestationResponse = request.Response,
                    DeviceName = request.DeviceName,
                    ExpectedSubject = expectedSubject
                };

                IB2BPasskeyService.RegistrationVerifyResult result;
                using (TimingScope.Begin("service_call"))
                {
                    result = await _passkeyService.VerifyRegistrationAsync(serviceRequest);
                }

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

                Activity.Current?.SetTag("client.id", client.ClientId);

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
                Client? client;
                using (TimingScope.Begin("client_authenticate"))
                {
                    client = await _context.Clients
                        .IgnoreQueryFilters()
                        .Include(c => c.RedirectUris)
                        .FirstOrDefaultAsync(c => c.ClientId == request.ClientId);
                }

                if (client == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", request.ClientId);
                    return Unauthorized(new
                    {
                        error = "invalid_client",
                        error_description = "クライアントが見つかりません。"
                    });
                }

                Activity.Current?.SetTag("client.id", client.ClientId);

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

                IB2BPasskeyService.AuthenticationVerifyResult result;
                using (TimingScope.Begin("service_call"))
                {
                    result = await _passkeyService.VerifyAuthenticationAsync(serviceRequest);
                }

                if (!result.Success)
                {
                    _logger.LogWarning("B2B Passkey authentication verification failed: {ErrorMessage}", result.ErrorMessage);
                    return BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = result.ErrorMessage ?? "パスキー認証の検証に失敗しました。"
                    });
                }

                // PKCE (RFC 7636) の検証。認可コードへ束縛する前に行う。
                // ここで弾かないと、形式不正は AuthorizationCodeService の ArgumentException
                // 経由で 500 になる（この経路は catch (Exception) しか持たないため）。
                if (string.IsNullOrEmpty(request.CodeChallenge))
                {
                    if (Security.PkcePolicy.IsRequired(_configuration))
                    {
                        _logger.LogWarning("PKCE required but code_challenge missing for client: {ClientId}", request.ClientId);
                        return BadRequest(new
                        {
                            error = "invalid_request",
                            error_description = "code_challenge が必要です。"
                        });
                    }
                }
                else
                {
                    if (!Security.PkceValidator.IsValidChallengeFormat(request.CodeChallenge))
                    {
                        _logger.LogWarning("Invalid code_challenge format for client: {ClientId}", request.ClientId);
                        return BadRequest(new
                        {
                            error = "invalid_request",
                            error_description = "code_challenge の形式が不正です。"
                        });
                    }

                    if (!Security.PkceValidator.IsSupportedMethod(request.CodeChallengeMethod))
                    {
                        _logger.LogWarning("Unsupported code_challenge_method: {Method}", request.CodeChallengeMethod);
                        return BadRequest(new
                        {
                            error = "invalid_request",
                            error_description = "code_challenge_method は S256 のみサポートします。"
                        });
                    }
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
                    // Client の SubjectType を反映（B2B 管理画面 = B2B、Account 管理コンソール = Account）。
                    // 認証機構（パスキー）は共通のため、認可コードの種別は Client 定義に従う。
                    SubjectType = client.SubjectType,
                    // PKCE: public client（マイページ等）が指定した場合のみ束縛する。
                    CodeChallenge = request.CodeChallenge,
                    CodeChallengeMethod = request.CodeChallengeMethod
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
        /// <summary>
        /// 登録トークンによる認可（accounts の初回パスキー登録・public client 経路）。
        /// トークンを検証して対象 Subject を得、public な Account コンソール client を
        /// client_secret 無しで解決し、external_id を confirm 済み B2BUser から確定する。
        /// 無効・不一致なら null。
        /// </summary>
        private async Task<(Client Client, string Subject, string ExternalId, string? BoundSessionId)?> AuthorizeByRegistrationTokenAsync(
            string clientId, string registrationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(registrationToken))
            {
                return null;
            }

            var tokenInfo = await _registrationTokenService.ValidateAsync(registrationToken);
            if (tokenInfo == null)
            {
                return null;
            }
            var subject = tokenInfo.Subject;

            // public な Account コンソール client を client_id で解決（secret 不要）。
            var client = await _context.Clients
                .IgnoreQueryFilters()
                .Include(c => c.RedirectUris)
                .FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client == null || client.SubjectType != SubjectType.Account)
            {
                return null;
            }

            // external_id は confirm で作成済みの B2BUser（Subject 共有）から解決する。
            var b2bUser = await _b2bUserService.GetBySubjectAsync(subject);
            if (b2bUser == null)
            {
                return null;
            }

            // トークンを発行元テナントへ束縛する。SubjectType.Account の確認だけでは、
            // 別の Account 種別 Client（例: accounts のトークンを stg-accounts コンソールへ）へ
            // 持ち込めてしまうため、提示された client の Organization が対象アカウントの
            // 所属 Organization と一致することを必須にする。
            if (client.OrganizationId == null || client.OrganizationId != b2bUser.OrganizationId)
            {
                _logger.LogWarning(
                    "Registration token presented to a client of a different organization. ClientId={ClientId}",
                    client.ClientId);
                return null;
            }

            return (client, subject, b2bUser.ExternalId, tokenInfo.SessionId);
        }

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

            // 保存値は Key Vault 暗号化エンベロープ（レガシーは平文）。ISecretProtector が
            // 復号(+キャッシュ)して定数時間比較を行う（タイミング攻撃対策）。
            if (await _secretProtector.VerifyAsync(clientSecret, client.ClientSecret))
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
