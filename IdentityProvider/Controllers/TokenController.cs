using IdentityProvider.Models;
using IdentityProvider.Services;
using IdpUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.ComponentModel.DataAnnotations;

namespace IdentityProvider.Controllers
{
    [Route("token")]
    [ApiController]
    public class TokenController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly IHostEnvironment _environment;
        private readonly ITokenService _tokenService;
        private readonly IUserService _userService;
        private readonly IB2BUserService _b2bUserService;
        private readonly ILogger<TokenController> _logger;
        private readonly IConfiguration _configuration;

        public TokenController(
            EcAuthDbContext context,
            IHostEnvironment environment,
            ITokenService tokenService,
            IUserService userService,
            IB2BUserService b2bUserService,
            ILogger<TokenController> logger,
            IConfiguration configuration)
        {
            _context = context;
            _environment = environment;
            _tokenService = tokenService;
            _userService = userService;
            _b2bUserService = b2bUserService;
            _logger = logger;
            _configuration = configuration;
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
            var password = Environment.GetEnvironmentVariable("STATE_PASSWORD");
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
                        { "redirect_uri", _configuration["DEFAULT_ORGANIZATION_REDIRECT_URI"] ?? "https://localhost:8081/auth/callback" },
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
            [FromForm] string? client_secret)
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
                var client = await _context.Clients
                    .FirstOrDefaultAsync(c => c.ClientId == client_id);

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
                if (!string.IsNullOrEmpty(client.ClientSecret))
                {
                    if (string.IsNullOrEmpty(client_secret) || client.ClientSecret != client_secret)
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
                var authorizationCode = await _context.AuthorizationCodes
                    .FirstOrDefaultAsync(ac => ac.Code == code);

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

                // 10. 認可コードを使用済みにマーキング
                authorizationCode.IsUsed = true;
                authorizationCode.UsedAt = DateTimeOffset.UtcNow;
                await _context.SaveChangesAsync();

                // 11. SubjectType に応じたユーザー情報の取得
                var subjectType = authorizationCode.SubjectType;
                var subject = authorizationCode.Subject;

                _logger.LogInformation("Fetching user for subject: {Subject}, SubjectType: {SubjectType}", subject, subjectType);

                // 12. トークン生成リクエストの構築
                var scopes = string.IsNullOrEmpty(authorizationCode.Scope)
                    ? null
                    : authorizationCode.Scope.Split(' ');

                ITokenService.TokenRequest tokenRequest;

                if (subjectType == SubjectType.B2B)
                {
                    // B2B認証の場合: B2BUserを取得
                    var b2bUser = await _b2bUserService.GetBySubjectAsync(subject!);
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
                        User = null!, // B2B認証では使用しない
                        Client = client,
                        RequestedScopes = scopes,
                        SubjectType = SubjectType.B2B,
                        B2BSubject = b2bUser.Subject
                    };

                    _logger.LogInformation("Token request created for B2B user: {Subject}, client: {ClientId}, scopes: {Scopes}",
                        b2bUser.Subject, client.ClientId, string.Join(", ", scopes ?? new[] { "none" }));
                }
                else
                {
                    // B2C認証の場合: EcAuthUserを取得（従来の処理）
                    var user = await _userService.GetUserBySubjectAsync(subject);
                    if (user == null)
                    {
                        _logger.LogError("User not found for subject: {Subject}", subject);

                        // デバッグ用: EcAuthUsersテーブルの全件を表示
                        var allUsers = await _context.EcAuthUsers.Select(u => new { u.Subject, u.OrganizationId }).ToListAsync();
                        _logger.LogError("All users in database: {Users}",
                            string.Join(", ", allUsers.Select(u => $"Subject={u.Subject}, OrgId={u.OrganizationId}")));

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

                // 13. トークンの生成
                ITokenService.TokenResponse tokenResponse;
                try
                {
                    tokenResponse = await _tokenService.GenerateTokensAsync(tokenRequest);
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
