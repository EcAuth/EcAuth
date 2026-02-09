using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace IdentityProvider.Services
{
    public class TokenService : ITokenService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<TokenService> _logger;

        public TokenService(EcAuthDbContext context, ILogger<TokenService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ITokenService.TokenResponse> GenerateTokensAsync(ITokenService.TokenRequest request)
        {
            var idToken = await GenerateIdTokenAsync(request);
            var accessToken = await GenerateAccessTokenAsync(request);

            return new ITokenService.TokenResponse
            {
                IdToken = idToken,
                AccessToken = accessToken,
                ExpiresIn = 3600, // 1時間
                TokenType = "Bearer"
            };
        }

        public async Task<string> GenerateIdTokenAsync(ITokenService.TokenRequest request)
        {
            if (request.Client == null)
                throw new ArgumentException("Client cannot be null.", nameof(request.Client));

            if (request.User == null)
                throw new ArgumentException("User cannot be null.", nameof(request.User));

            if (request.SubjectType != SubjectType.B2C && request.SubjectType != SubjectType.B2B)
                throw new ArgumentException($"Unsupported SubjectType: {request.SubjectType}", nameof(request.SubjectType));

            // ISubjectProvider から Subject を取得（B2C/B2B 共通）
            var subject = request.User.Subject;
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject cannot be null or empty.", nameof(request.User));

            // RSA鍵ペアを取得
            var rsaKeyPair = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(k => k.ClientId == request.Client.Id);

            if (rsaKeyPair == null)
                throw new InvalidOperationException($"RSA key pair not found for client {request.Client.Id}");

            using (var rsa = RSA.Create())
            {
                try
                {
                    rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to import RSA private key for client {request.Client.Id}: {ex.Message}", ex);
                }

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var now = DateTime.UtcNow;
                var expires = now.AddHours(1);

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, subject),
                    new(JwtRegisteredClaimNames.Iss, GetIssuer()),
                    new(JwtRegisteredClaimNames.Aud, request.Client.ClientId),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(expires).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
                };

                // nonceが指定されている場合は追加
                if (!string.IsNullOrEmpty(request.Nonce))
                {
                    claims.Add(new Claim(JwtRegisteredClaimNames.Nonce, request.Nonce));
                }

                // 追加のクレーム（スコープに基づいて）
                if (request.RequestedScopes?.Contains("email") == true)
                {
                    // メールアドレスはハッシュ化されているため、実際のメールアドレスは返さない
                    // 必要に応じて外部IdPから取得した情報を含める実装を検討
                    claims.Add(new Claim("email_verified", "true"));
                }

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = expires,
                    NotBefore = now,
                    IssuedAt = now,
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);
                

                return tokenString;
            }
        }

        public async Task<string> GenerateAccessTokenAsync(ITokenService.TokenRequest request)
        {
            if (request.Client == null)
                throw new ArgumentException("Client cannot be null.", nameof(request.Client));

            if (request.User == null)
                throw new ArgumentException("User cannot be null.", nameof(request.User));

            if (request.SubjectType != SubjectType.B2C && request.SubjectType != SubjectType.B2B)
                throw new ArgumentException($"Unsupported SubjectType: {request.SubjectType}", nameof(request.SubjectType));

            // ISubjectProvider から Subject を取得（B2C/B2B 共通）
            var subject = request.User.Subject;
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject cannot be null or empty.", nameof(request.User));

            // RSA鍵ペアを取得
            var rsaKeyPair = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(k => k.ClientId == request.Client.Id);

            if (rsaKeyPair == null)
                throw new InvalidOperationException($"RSA key pair not found for client {request.Client.Id}");

            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;
            var expiresAt = now.AddHours(1);
            var scopes = request.RequestedScopes != null ? string.Join(" ", request.RequestedScopes) : null;

            // JWT を生成
            string accessTokenJwt;
            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var subjectTypeString = request.SubjectType == SubjectType.B2B ? "b2b" : "b2c";

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, subject),
                    new("sub_type", subjectTypeString),
                    new("org_id", request.Client.OrganizationId.ToString(), ClaimValueTypes.Integer32),
                    new("client_id", request.Client.ClientId),
                    new(JwtRegisteredClaimNames.Iss, GetIssuer()),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(expiresAt).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, jti)
                };

                if (!string.IsNullOrEmpty(scopes))
                {
                    claims.Add(new Claim("scope", scopes));
                }

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = expiresAt,
                    NotBefore = now,
                    IssuedAt = now,
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                accessTokenJwt = tokenHandler.WriteToken(token);
            }

            // メタデータをデータベースに保存（Token カラムには jti のみ）
            var accessTokenEntity = new AccessToken
            {
                Token = jti,
                ExpiresAt = expiresAt,
                ClientId = request.Client.Id,
                Subject = subject,
                SubjectType = request.SubjectType,
                CreatedAt = now,
                Scopes = scopes
            };

            _context.AccessTokens.Add(accessTokenEntity);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Access token generated for subject {Subject} (type: {SubjectType}) and client {ClientId}",
                subject, request.SubjectType, request.Client.Id);

            return accessTokenJwt;
        }

        public async Task<string?> ValidateTokenAsync(string token, int clientId)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();

                // クライアントのRSA公開鍵を取得
                var rsaKeyPair = await _context.RsaKeyPairs
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(k => k.ClientId == clientId);

                if (rsaKeyPair == null)
                {
                    _logger.LogWarning("RSA key pair not found for client {ClientId}", clientId);
                    return null;
                }

                using (var rsa = RSA.Create())
                {
                    try
                    {
                        // 検証では公開鍵を使用
                        rsa.ImportRSAPublicKey(Convert.FromBase64String(rsaKeyPair.PublicKey), out _);
                    }
                    catch (Exception)
                    {
                        // 公開鍵のインポートに失敗した場合、秘密鍵から公開コンポーネントのみを抽出
                        try
                        {
                            using var privateRsa = RSA.Create();
                            privateRsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);
                            var publicParameters = privateRsa.ExportParameters(false);
                            rsa.ImportParameters(publicParameters);
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to import RSA keys for client {ClientId}", clientId);
                            return null;
                        }
                    }

                    var validationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new RsaSecurityKey(rsa),
                        ValidateIssuer = true,
                        ValidIssuer = GetIssuer(),
                        ValidateAudience = false, // We'll validate audience manually later
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(5),
                        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                    };

                    var principal = tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
                    var subjectClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub) ?? 
                                     principal.FindFirst("sub") ?? 
                                     principal.FindFirst(ClaimTypes.NameIdentifier);

                    return subjectClaim?.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token validation failed");
                return null;
            }
        }

        private static string GetIssuer()
        {
            // 実際の実装では設定ファイルから取得
            return "https://ecauth.example.com";
        }

        public async Task<string?> ValidateAccessTokenAsync(string token)
        {
            var result = await ValidateAccessTokenWithTypeAsync(token);
            return result.IsValid ? result.Subject : null;
        }

        public async Task<ITokenService.AccessTokenValidationResult> ValidateAccessTokenWithTypeAsync(string token)
        {
            try
            {
                // 1. JWT をデコードして client_id を取得
                var tokenHandler = new JwtSecurityTokenHandler();
                JwtSecurityToken jwtToken;
                try
                {
                    jwtToken = tokenHandler.ReadJwtToken(token);
                }
                catch (Exception)
                {
                    _logger.LogWarning("Access token is not a valid JWT");
                    return new ITokenService.AccessTokenValidationResult { IsValid = false };
                }

                var clientIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value;
                if (string.IsNullOrEmpty(clientIdClaim))
                {
                    _logger.LogWarning("Access token does not contain client_id claim");
                    return new ITokenService.AccessTokenValidationResult { IsValid = false };
                }

                // 2. Client を検索
                var client = await _context.Clients
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.ClientId == clientIdClaim);

                if (client == null)
                {
                    _logger.LogWarning("Client not found for client_id: {ClientId}", clientIdClaim);
                    return new ITokenService.AccessTokenValidationResult { IsValid = false };
                }

                // 3. RSA 公開鍵を取得
                var rsaKeyPair = await _context.RsaKeyPairs
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(k => k.ClientId == client.Id);

                if (rsaKeyPair == null)
                {
                    _logger.LogWarning("RSA key pair not found for client {ClientId}", client.Id);
                    return new ITokenService.AccessTokenValidationResult { IsValid = false };
                }

                // 4. JWT 署名検証 + exp チェック
                using (var rsa = RSA.Create())
                {
                    try
                    {
                        rsa.ImportRSAPublicKey(Convert.FromBase64String(rsaKeyPair.PublicKey), out _);
                    }
                    catch (Exception)
                    {
                        // 公開鍵のインポートに失敗した場合、秘密鍵から公開コンポーネントのみを抽出
                        try
                        {
                            using var privateRsa = RSA.Create();
                            privateRsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);
                            var publicParameters = privateRsa.ExportParameters(false);
                            rsa.ImportParameters(publicParameters);
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to import RSA keys for client {ClientId}", client.Id);
                            return new ITokenService.AccessTokenValidationResult { IsValid = false };
                        }
                    }

                    var validationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new RsaSecurityKey(rsa),
                        ValidateIssuer = true,
                        ValidIssuer = GetIssuer(),
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(5),
                        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                    };

                    try
                    {
                        tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Access token JWT validation failed");
                        return new ITokenService.AccessTokenValidationResult { IsValid = false };
                    }
                }

                // 5. jti 失効チェック
                var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
                if (!string.IsNullOrEmpty(jti))
                {
                    var isRevoked = await _context.AccessTokens
                        .IgnoreQueryFilters()
                        .AnyAsync(at => at.Token == jti && at.IsRevoked);

                    if (isRevoked)
                    {
                        _logger.LogWarning("Access token has been revoked: jti={Jti}", jti);
                        return new ITokenService.AccessTokenValidationResult { IsValid = false };
                    }
                }

                // 6. Claims から情報を抽出
                var subject = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
                var subTypeClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "sub_type")?.Value;
                var scopeClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "scope")?.Value;
                var orgIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "org_id")?.Value;

                SubjectType subjectType = subTypeClaim == "b2b" ? SubjectType.B2B : SubjectType.B2C;

                _logger.LogDebug("Access token validated successfully for subject {Subject} (type: {SubjectType})",
                    subject, subjectType);

                return new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = subject,
                    SubjectType = subjectType,
                    ClientId = clientIdClaim,
                    OrganizationId = int.TryParse(orgIdClaim, out var orgId) ? orgId : null,
                    Jti = jti,
                    Scopes = scopeClaim
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Access token validation failed");
                return new ITokenService.AccessTokenValidationResult { IsValid = false };
            }
        }

        public async Task<bool> RevokeAccessTokenAsync(string token)
        {
            try
            {
                // JWT をデコードして jti を取得
                string jti;
                try
                {
                    var tokenHandler = new JwtSecurityTokenHandler();
                    var jwtToken = tokenHandler.ReadJwtToken(token);
                    jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value
                        ?? string.Empty;
                }
                catch (Exception)
                {
                    _logger.LogWarning("Failed to decode JWT for revocation");
                    return false;
                }

                if (string.IsNullOrEmpty(jti))
                {
                    _logger.LogWarning("JWT does not contain jti claim for revocation");
                    return false;
                }

                // jti で AccessToken レコードを検索
                var accessToken = await _context.AccessTokens
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(at => at.Token == jti);

                if (accessToken == null)
                {
                    _logger.LogWarning("Access token not found for revocation: jti={Jti}", jti);
                    return false;
                }

                // 論理削除（IsRevoked フラグを設定）
                accessToken.IsRevoked = true;
                accessToken.RevokedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Access token revoked successfully: jti={Jti}", jti);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke access token");
                return false;
            }
        }

        private async Task<string> GetClientIdStringAsync(int clientId)
        {
            var client = await _context.Clients.FindAsync(clientId);
            return client?.ClientId ?? clientId.ToString();
        }
    }
}