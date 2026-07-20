using IdentityProvider.Filters;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Telemetry;
using IdpUtilities;
using IdpUtilities.Security;
using Asp.Versioning;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.ComponentModel.DataAnnotations;

namespace IdentityProvider.Controllers
{
    // マイページ（ec-auth.io）が public client（PKCE）としてトークン交換するため、
    // 申込 API と同じ SignupApiCors（ec-auth.io / www）を許可する。
    [Route("v{version:apiVersion}/token")]
    [ApiController]
    [ApiVersion("1.0")]
    [EnableCors(SignupController.CorsPolicy)]
    // RFC 6749 §5.1: token endpoint のレスポンスはキャッシュさせない。
    [NoStore]
    public class TokenController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly IHostEnvironment _environment;
        private readonly ITokenService _tokenService;
        private readonly IUserService _userService;
        private readonly IB2BUserService _b2bUserService;
        private readonly IAccountService _accountService;
        private readonly ILogger<TokenController> _logger;
        private readonly IConfiguration _configuration;
        private readonly ISecretProtector _secretProtector;

        public TokenController(
            EcAuthDbContext context,
            IHostEnvironment environment,
            ITokenService tokenService,
            IUserService userService,
            IB2BUserService b2bUserService,
            IAccountService accountService,
            ILogger<TokenController> logger,
            IConfiguration configuration,
            ISecretProtector secretProtector)
        {
            _context = context;
            _environment = environment;
            _tokenService = tokenService;
            _userService = userService;
            _b2bUserService = b2bUserService;
            _accountService = accountService;
            _logger = logger;
            _configuration = configuration;
            _secretProtector = secretProtector;
        }

        /// <summary>
        /// IdP の Token endpoint にリクエストを送信し、アクセストークンを取得します。
        /// 取得したアクセストークンを返却します。
        /// </summary>
        /// <param name="code"></param>
        /// <param name="state"></param>
        /// <param name="scope"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("exchange")]
        public async Task<IActionResult> Exchange([FromForm] string code, [FromForm] string state, [FromForm] string scope)
        {
            // seal 側（AuthorizationController）と解決経路を揃える。
            var password = _configuration["STATE_PASSWORD"];
            var options = new Iron.Options();
            var State = await Iron.Unseal<State>(state, password, options);
            var IdentityProviderId = State.OpenIdProviderId;
            var IdentityProvider = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .Where(p => p.Id == IdentityProviderId)
                .FirstOrDefaultAsync();

            var handler = _environment.IsDevelopment()
                ? new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback
                        = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }
                : new HttpClientHandler();

            using (var client = new HttpClient(handler))
            {
                var response = await client.PostAsync(
                    IdentityProvider.TokenEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "grant_type", "authorization_code" },
                        { "code", code },
                        { "redirect_uri", _configuration["DEFAULT_ORGANIZATION_REDIRECT_URI"] ?? "https://localhost:8081/v1/auth/callback" },
                        { "client_id", IdentityProvider.IdpClientId },
                        { "client_secret", IdentityProvider.IdpClientSecret },
                        { "state", state }
                    })
                );

                var content = await response.Content.ReadAsStringAsync();
                return Ok(content);
            }
        }

        /// <summary>
        /// OpenID Connect準拠のToken endpoint
        /// 認可コードをIDトークンとアクセストークンに交換します
        /// </summary>
        /// <param name="grant_type">グラントタイプ（"authorization_code"のみサポート）</param>
        /// <param name="code">認可コード</param>
        /// <param name="redirect_uri">リダイレクトURI</param>
        /// <param name="client_id">クライアントID</param>
        /// <param name="client_secret">クライアントシークレット</param>
        /// <returns>トークンレスポンス</returns>
        [HttpPost]
        [Route("")]
        public async Task<IActionResult> Token(
            [FromForm, Required] string grant_type,
            [FromForm, Required] string code,
            [FromForm, Required] string redirect_uri,
            [FromForm, Required] string client_id,
            [FromForm] string? client_secret,
            [FromForm] string? code_verifier = null)
        {
            try
            {
                _logger.LogInformation("Token endpoint accessed with grant_type: {GrantType}, client_id: {ClientId}", grant_type, client_id);

                // 1. grant_typeの検証
                if (grant_type != "authorization_code")
                {
                    _logger.LogWarning("Unsupported grant_type: {GrantType}", grant_type);
                    return BadRequest(new
                    {
                        error = "unsupported_grant_type",
                        error_description = "grant_typeはauthorization_codeのみサポートしています。"
                    });
                }

                // 2. クライアントの存在確認
                Client? client;
                using (TimingScope.Begin("client_lookup"))
                {
                    client = await _context.Clients
                        .FirstOrDefaultAsync(c => c.ClientId == client_id);
                }

                if (client == null)
                {
                    _logger.LogWarning("Client not found: {ClientId}", client_id);
                    return BadRequest(new
                    {
                        error = "invalid_client",
                        error_description = "クライアントが見つかりません。"
                    });
                }

                // 4. client_secretの検証（設定されている場合のみ）
                // 保存値は Key Vault 暗号化エンベロープ（レガシーは平文）。ISecretProtector が
                // 復号(+キャッシュ)して定数時間比較を行う。提示値が空なら復号せず即座に拒否する。
                if (!string.IsNullOrEmpty(client.ClientSecret))
                {
                    bool secretValid;
                    using (TimingScope.Begin("client_secret_verify"))
                    {
                        secretValid = !string.IsNullOrEmpty(client_secret)
                            && await _secretProtector.VerifyAsync(client_secret, client.ClientSecret);
                    }
                    if (!secretValid)
                    {
                        _logger.LogWarning("Invalid client_secret for client: {ClientId}", client_id);
                        return BadRequest(new
                        {
                            error = "invalid_client",
                            error_description = "client_secretが正しくありません。"
                        });
                    }
                }

                // 5. 認可コードの取得・検証
                AuthorizationCode? authorizationCode;
                using (TimingScope.Begin("auth_code_lookup"))
                {
                    authorizationCode = await _context.AuthorizationCodes
                        .FirstOrDefaultAsync(ac => ac.Code == code);
                }

                if (authorizationCode == null)
                {
                    _logger.LogWarning("Authorization code not found: {Code}", code);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "認可コードが見つかりません。"
                    });
                }

                // 6. 認可コードの有効期限チェック
                if (authorizationCode.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning("Authorization code expired: {Code}, ExpiresAt: {ExpiresAt}", code, authorizationCode.ExpiresAt);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "認可コードの有効期限が切れています。"
                    });
                }

                // 7. 認可コードの使用済み状態チェック
                if (authorizationCode.IsUsed)
                {
                    _logger.LogWarning("Authorization code already used: {Code}, UsedAt: {UsedAt}", code, authorizationCode.UsedAt);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "認可コードは既に使用されています。"
                    });
                }

                // 8. redirect_uriの一致確認
                if (authorizationCode.RedirectUri != redirect_uri)
                {
                    _logger.LogWarning("Redirect URI mismatch. Expected: {Expected}, Provided: {Provided}", 
                        authorizationCode.RedirectUri, redirect_uri);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "redirect_uriが一致しません。"
                    });
                }

                // 9. クライアントIDの一致確認
                if (authorizationCode.ClientId != client.Id)
                {
                    _logger.LogWarning("Client ID mismatch. Expected: {Expected}, Provided: {Provided}", 
                        authorizationCode.ClientId, client.Id);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "client_idが一致しません。"
                    });
                }

                // 9.5. PKCE (RFC 7636) 検証
                // 認可コードに code_challenge が束縛されている場合は code_verifier 必須。
                // public client（client_secret 未設定）は PKCE を必須とし、code_challenge を
                // 持たない認可コードでのトークン交換を拒否する（認可コード横取り攻撃対策）。
                var isPublicClient = string.IsNullOrEmpty(client.ClientSecret);
                if (!string.IsNullOrEmpty(authorizationCode.CodeChallenge))
                {
                    if (string.IsNullOrEmpty(code_verifier))
                    {
                        _logger.LogWarning("PKCE code_verifier missing for client: {ClientId}", client_id);
                        return BadRequest(new
                        {
                            error = "invalid_grant",
                            error_description = "code_verifier が必要です。"
                        });
                    }

                    if (!Security.PkceValidator.Verify(code_verifier, authorizationCode.CodeChallenge, authorizationCode.CodeChallengeMethod))
                    {
                        _logger.LogWarning("PKCE verification failed for client: {ClientId}", client_id);
                        return BadRequest(new
                        {
                            error = "invalid_grant",
                            error_description = "code_verifier が一致しません。"
                        });
                    }
                }
                else if (isPublicClient)
                {
                    _logger.LogWarning("Public client without PKCE rejected: {ClientId}", client_id);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "public client では PKCE (code_challenge) が必須です。"
                    });
                }

                // 10. 認可コードを使用済みにマーキング
                authorizationCode.IsUsed = true;
                authorizationCode.UsedAt = DateTimeOffset.UtcNow;
                using (TimingScope.Begin("auth_code_mark_used"))
                {
                    await _context.SaveChangesAsync();
                }

                // 11. SubjectType に応じたユーザー情報の取得
                var subjectType = authorizationCode.SubjectType;
                var subject = authorizationCode.Subject;

                if (string.IsNullOrWhiteSpace(subject))
                {
                    _logger.LogError("Authorization code has empty subject. SubjectType: {SubjectType}", subjectType);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "認可コードのSubjectが不正です。"
                    });
                }

                _logger.LogInformation("Fetching user for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);

                // 12. トークン生成リクエストの構築
                var scopes = string.IsNullOrEmpty(authorizationCode.Scope)
                    ? null
                    : authorizationCode.Scope.Split(' ');

                ITokenService.TokenRequest tokenRequest;

                if (subjectType == SubjectType.B2B)
                {
                    // B2B認証の場合: B2BUserを取得
                    B2BUser? b2bUser;
                    using (TimingScope.Begin("user_lookup"))
                    {
                        b2bUser = await _b2bUserService.GetBySubjectAsync(subject);
                    }
                    if (b2bUser == null)
                    {
                        _logger.LogError("B2B user not found for subject: {Subject}", subject);
                        return BadRequest(new
                        {
                            error = "invalid_grant",
                            error_description = "B2Bユーザーが見つかりません。"
                        });
                    }

                    tokenRequest = new ITokenService.TokenRequest
                    {
                        User = b2bUser,
                        Client = client,
                        RequestedScopes = scopes,
                        SubjectType = SubjectType.B2B
                    };

                    _logger.LogInformation("Token request created for B2B user: {Subject}, client: {ClientId}, scopes: {Scopes}",
                        b2bUser.Subject, client.ClientId, string.Join(", ", scopes ?? new[] { "none" }));
                }
                else if (subjectType == SubjectType.B2C)
                {
                    // B2C認証の場合: EcAuthUserを取得（従来の処理）
                    EcAuthUser? user;
                    using (TimingScope.Begin("user_lookup"))
                    {
                        user = await _userService.GetUserBySubjectAsync(subject);
                    }
                    if (user == null)
                    {
                        _logger.LogError("User not found for subject: {Subject}", subject);
                        return BadRequest(new
                        {
                            error = "invalid_grant",
                            error_description = "ユーザーが見つかりません。"
                        });
                    }

                    tokenRequest = new ITokenService.TokenRequest
                    {
                        User = user,
                        Client = client,
                        RequestedScopes = scopes,
                        SubjectType = SubjectType.B2C
                    };

                    _logger.LogInformation("Token request created for user: {Subject}, client: {ClientId}, scopes: {Scopes}",
                        user.Subject, client.ClientId, string.Join(", ", scopes ?? new[] { "none" }));
                }
                else if (subjectType == SubjectType.Account)
                {
                    // Account認証の場合: Account を取得し、管理対象 Organization を managed_orgs に含める
                    Account? account;
                    using (TimingScope.Begin("user_lookup"))
                    {
                        account = await _accountService.GetBySubjectAsync(subject);
                    }
                    if (account == null)
                    {
                        _logger.LogError("Account not found for subject: {Subject}", subject);
                        return BadRequest(new
                        {
                            error = "invalid_grant",
                            error_description = "Accountが見つかりません。"
                        });
                    }

                    var managedOrgs = await _accountService.GetManagedOrganizationsAsync(subject);

                    tokenRequest = new ITokenService.TokenRequest
                    {
                        User = account,
                        Client = client,
                        RequestedScopes = scopes,
                        SubjectType = SubjectType.Account,
                        ManagedOrgs = managedOrgs
                    };

                    _logger.LogInformation("Token request created for account: {Subject}, client: {ClientId}, managedOrgs: {Count}, scopes: {Scopes}",
                        account.Subject, client.ClientId, managedOrgs.Count, string.Join(", ", scopes ?? new[] { "none" }));
                }
                else
                {
                    // サポートされていないSubjectType
                    _logger.LogError("Unsupported SubjectType: {SubjectType}", subjectType);
                    return BadRequest(new
                    {
                        error = "invalid_grant",
                        error_description = "サポートされていないSubjectTypeです。"
                    });
                }

                // 13. トークンの生成
                ITokenService.TokenResponse tokenResponse;
                try
                {
                    using (TimingScope.Begin("token_generate"))
                    {
                        tokenResponse = await _tokenService.GenerateTokensAsync(tokenRequest);
                    }
                    _logger.LogInformation("Tokens generated successfully for subject: {Subject}, SubjectType: {SubjectType}, client: {ClientId}",
                        subject, subjectType, client_id);
                }
                catch (Exception tokenEx)
                {
                    _logger.LogError(tokenEx, "Failed to generate tokens for subject: {Subject}, SubjectType: {SubjectType}",
                        subject, subjectType);
                    throw;
                }

                // 14. OpenID Connect準拠のレスポンス
                return Ok(new
                {
                    access_token = tokenResponse.AccessToken,
                    token_type = tokenResponse.TokenType,
                    expires_in = tokenResponse.ExpiresIn,
                    id_token = tokenResponse.IdToken,
                    refresh_token = tokenResponse.RefreshToken
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Token endpoint - Message: {Message}, StackTrace: {StackTrace}",
                    ex.Message, ex.StackTrace);

                // Development環境では詳細なエラー情報を返す
                if (_environment.IsDevelopment())
                {
                    return StatusCode(500, new
                    {
                        error = "server_error",
                        error_description = $"サーバー内部エラーが発生しました。詳細: {ex.Message}",
                        debug_info = new
                        {
                            exception_type = ex.GetType().Name,
                            message = ex.Message,
                            stack_trace = ex.StackTrace
                        }
                    });
                }

                return StatusCode(500, new
                {
                    error = "server_error",
                    error_description = "サーバー内部エラーが発生しました。"
                });
            }
        }
    }
}
