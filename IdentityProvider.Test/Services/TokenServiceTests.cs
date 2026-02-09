using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace IdentityProvider.Test.Services
{
    public class TokenServiceTests
    {
        private readonly ILogger<TokenService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public const string TestIssuer = "https://test.ec-cube.io";

        public TokenServiceTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<TokenService>();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("test.ec-cube.io");
            _httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        }

        [Fact]
        public async Task GenerateIdTokenAsync_ValidRequest_ShouldGenerateValidJwtToken()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "email" },
                Nonce = "test-nonce"
            };

            // Act
            var token = await service.GenerateIdTokenAsync(request);

            // Assert
            Assert.NotNull(token);
            Assert.NotEmpty(token);

            // Validate JWT structure
            var handler = new JwtSecurityTokenHandler();
            var jsonToken = handler.ReadJwtToken(token);

            Assert.Equal(user.Subject, jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);
            Assert.Equal(TestIssuer, jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value);
            Assert.Equal(client.ClientId, jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Aud)?.Value);
            Assert.Equal("test-nonce", jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Nonce)?.Value);
            Assert.NotNull(jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value);
            Assert.Equal("true", jsonToken.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value);
        }

        [Fact]
        public async Task GenerateIdTokenAsync_WithoutNonce_ShouldGenerateTokenWithoutNonceClaim()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid" }
            };

            // Act
            var token = await service.GenerateIdTokenAsync(request);

            // Assert
            var handler = new JwtSecurityTokenHandler();
            var jsonToken = handler.ReadJwtToken(token);

            Assert.Null(jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Nonce));
        }

        [Fact]
        public async Task GenerateIdTokenAsync_WithoutEmailScope_ShouldNotIncludeEmailClaims()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid" }
            };

            // Act
            var token = await service.GenerateIdTokenAsync(request);

            // Assert
            var handler = new JwtSecurityTokenHandler();
            var jsonToken = handler.ReadJwtToken(token);

            Assert.Null(jsonToken.Claims.FirstOrDefault(c => c.Type == "email_verified"));
        }

        [Fact]
        public async Task GenerateIdTokenAsync_NullUser_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, _, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = null!,
                Client = client
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateIdTokenAsync(request));
        }

        [Fact]
        public async Task GenerateIdTokenAsync_NullClient_ShouldThrowArgumentException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (_, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = null!
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateIdTokenAsync(request));
        }

        [Fact]
        public async Task GenerateIdTokenAsync_NoRsaKeyPair_ShouldThrowInvalidOperationException()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange - Create client and user without RSA key pair
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };
            context.EcAuthUsers.Add(user);
            await context.SaveChangesAsync();

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client
            };

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateIdTokenAsync(request));
        }

        [Fact]
        public async Task GenerateAccessTokenAsync_ValidRequest_ShouldGenerateAccessToken()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client
            };

            // Act
            var accessToken = await service.GenerateAccessTokenAsync(request);

            // Assert
            Assert.NotNull(accessToken);
            Assert.NotEmpty(accessToken);

            // JWT 形式（3パート、ドット区切り）であることを確認
            var parts = accessToken.Split('.');
            Assert.Equal(3, parts.Length);

            // JWT としてデコード可能であることを確認
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);
            Assert.Equal(user.Subject, jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);
        }

        [Fact]
        public async Task GenerateAccessTokenAsync_ShouldContainCorrectJwtClaims()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };

            // Act
            var accessToken = await service.GenerateAccessTokenAsync(request);

            // Assert
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);

            Assert.Equal(user.Subject, jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);
            Assert.Equal("b2c", jwtToken.Claims.FirstOrDefault(c => c.Type == "sub_type")?.Value);
            Assert.Equal(client.OrganizationId.ToString(), jwtToken.Claims.FirstOrDefault(c => c.Type == "org_id")?.Value);
            Assert.Equal(client.ClientId, jwtToken.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value);
            Assert.Equal(TestIssuer, jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value);
            Assert.NotNull(jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value);
            Assert.Equal("openid profile", jwtToken.Claims.FirstOrDefault(c => c.Type == "scope")?.Value);
        }

        [Fact]
        public async Task GenerateTokensAsync_ValidRequest_ShouldGenerateBothTokens()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "email" },
                Nonce = "test-nonce"
            };

            // Act
            var response = await service.GenerateTokensAsync(request);

            // Assert
            Assert.NotNull(response);
            Assert.NotEmpty(response.IdToken);
            Assert.NotEmpty(response.AccessToken);
            Assert.Equal(3600, response.ExpiresIn);
            Assert.Equal("Bearer", response.TokenType);

            // Validate ID token is JWT
            var handler = new JwtSecurityTokenHandler();
            var idJsonToken = handler.ReadJwtToken(response.IdToken);
            Assert.Equal(user.Subject, idJsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);

            // Validate Access token is also JWT
            var accessJsonToken = handler.ReadJwtToken(response.AccessToken);
            Assert.Equal(user.Subject, accessJsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);
        }

        [Fact]
        public async Task ValidateTokenAsync_ValidToken_ShouldReturnSubject()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client
            };

            var token = await service.GenerateIdTokenAsync(request);

            // Act
            var subject = await service.ValidateTokenAsync(token, client.Id);

            // Assert
            Assert.Equal(user.Subject, subject);
        }

        [Fact]
        public async Task ValidateTokenAsync_InvalidToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, _, _) = await SetupTestDataAsync(context);

            // Act
            var subject = await service.ValidateTokenAsync("invalid-token", client.Id);

            // Assert
            Assert.Null(subject);
        }

        [Fact]
        public async Task ValidateTokenAsync_NoRsaKeyPair_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange - Create client without RSA key pair
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            context.Clients.Add(client);
            await context.SaveChangesAsync();

            // Act
            var subject = await service.ValidateTokenAsync("some-token", client.Id);

            // Assert
            Assert.Null(subject);
        }

        private async Task<(Client client, EcAuthUser user, RsaKeyPair rsaKeyPair)> SetupTestDataAsync(EcAuthDbContext context)
        {
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = EmailHashUtil.HashEmail("test@example.com"),
                OrganizationId = 1
            };
            context.EcAuthUsers.Add(user);

            var rsaKeyPair = TestDbContextHelper.GenerateAndAddRsaKeyPair(context, client, 1);

            await context.SaveChangesAsync();

            return (client, user, rsaKeyPair);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_ValidToken_ShouldReturnSubject()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid" }
            };

            var accessToken = await service.GenerateAccessTokenAsync(request);

            // Act
            var subject = await service.ValidateAccessTokenAsync(accessToken);

            // Assert
            Assert.Equal(user.Subject, subject);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_InvalidToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Act
            var subject = await service.ValidateAccessTokenAsync("invalid-token");

            // Assert
            Assert.Null(subject);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_ExpiredToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            // 期限切れの JWT を手動生成
            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, user.Subject),
                    new("sub_type", "b2c"),
                    new("org_id", client.OrganizationId.ToString(), ClaimValueTypes.Integer32),
                    new("client_id", client.ClientId),
                    new(JwtRegisteredClaimNames.Iss, TestIssuer),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now.AddHours(-2)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(now.AddHours(-1)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, jti)
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = now.AddHours(-1), // 1時間前に期限切れ
                    NotBefore = now.AddHours(-2),
                    IssuedAt = now.AddHours(-2),
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var expiredJwt = tokenHandler.WriteToken(token);

                // DB にメタデータを保存
                context.AccessTokens.Add(new AccessToken
                {
                    Token = jti,
                    ExpiresAt = now.AddHours(-1),
                    ClientId = client.Id,
                    Subject = user.Subject,
                    CreatedAt = now.AddHours(-2)
                });
                await context.SaveChangesAsync();

                // Act
                var subject = await service.ValidateAccessTokenAsync(expiredJwt);

                // Assert
                Assert.Null(subject);
            }
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_RevokedJwt_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid" }
            };

            var accessToken = await service.GenerateAccessTokenAsync(request);

            // 失効させる
            var revokeResult = await service.RevokeAccessTokenAsync(accessToken);
            Assert.True(revokeResult);

            // Act
            var subject = await service.ValidateAccessTokenAsync(accessToken);

            // Assert
            Assert.Null(subject);
        }

        [Fact]
        public async Task RevokeAccessTokenAsync_ValidToken_ShouldRevokeSuccessfully()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client
            };

            var accessToken = await service.GenerateAccessTokenAsync(request);

            // JWT から jti を取得
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);
            var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

            // Act
            var result = await service.RevokeAccessTokenAsync(accessToken);

            // Assert
            Assert.True(result);

            // IsRevoked フラグが設定されていることを確認
            var revokedToken = await context.AccessTokens.FirstOrDefaultAsync(at => at.Token == jti);
            Assert.NotNull(revokedToken);
            Assert.True(revokedToken.IsRevoked);
            Assert.NotNull(revokedToken.RevokedAt);
        }

        [Fact]
        public async Task RevokeAccessTokenAsync_InvalidToken_ShouldReturnFalse()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Act
            var result = await service.RevokeAccessTokenAsync("invalid-token");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_DifferentClientKey_ShouldReturnInvalid()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange - Client A のデータを作成
            var (clientA, user, rsaKeyPairA) = await SetupTestDataAsync(context);

            // Client B を別の Organization + RSA鍵ペアで作成
            var organizationB = new Organization { Id = 2, Code = "TESTORG2", Name = "TestOrg2", TenantName = "test-tenant-2" };
            context.Organizations.Add(organizationB);

            var clientB = new Client
            {
                Id = 2,
                ClientId = "test-client-b",
                ClientSecret = "test-secret-b",
                AppName = "Test App B",
                OrganizationId = 2
            };
            context.Clients.Add(clientB);
            TestDbContextHelper.GenerateAndAddRsaKeyPair(context, clientB, 2);
            await context.SaveChangesAsync();

            // Client A の鍵で署名した JWT を手動生成し、client_id を Client B に設定
            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPairA.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, user.Subject),
                    new("sub_type", "b2c"),
                    new("org_id", clientB.OrganizationId.ToString()!, ClaimValueTypes.Integer32),
                    new("client_id", clientB.ClientId), // Client B の ClientId を設定
                    new(JwtRegisteredClaimNames.Iss, TestIssuer),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, jti)
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = now.AddHours(1),
                    NotBefore = now,
                    IssuedAt = now,
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tamperedJwt = tokenHandler.WriteToken(token);

                // Act - Client B の鍵で署名検証が走るため、署名不一致で失敗するはず
                var result = await service.ValidateAccessTokenWithTypeAsync(tamperedJwt);

                // Assert
                Assert.False(result.IsValid);
            }
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_WrongIssuer_ShouldReturnInvalid()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, user.Subject),
                    new("sub_type", "b2c"),
                    new("org_id", client.OrganizationId.ToString()!, ClaimValueTypes.Integer32),
                    new("client_id", client.ClientId),
                    new(JwtRegisteredClaimNames.Iss, "https://malicious.example.com"), // 不正な Issuer
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, jti)
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = now.AddHours(1),
                    NotBefore = now,
                    IssuedAt = now,
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var wrongIssuerJwt = tokenHandler.WriteToken(token);

                // DB にメタデータを保存
                context.AccessTokens.Add(new AccessToken
                {
                    Token = jti,
                    ExpiresAt = now.AddHours(1),
                    ClientId = client.Id,
                    Subject = user.Subject,
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                // Act
                var result = await service.ValidateAccessTokenWithTypeAsync(wrongIssuerJwt);

                // Assert
                Assert.False(result.IsValid);
            }
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_MissingJtiClaim_ShouldReturnInvalid()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, rsaKeyPair) = await SetupTestDataAsync(context);

            var now = DateTime.UtcNow;

            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                // jti を意図的に除外
                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, user.Subject),
                    new("sub_type", "b2c"),
                    new("org_id", client.OrganizationId.ToString()!, ClaimValueTypes.Integer32),
                    new("client_id", client.ClientId),
                    new(JwtRegisteredClaimNames.Iss, TestIssuer),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(now.AddHours(1)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
                    // jti なし
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = now.AddHours(1),
                    NotBefore = now,
                    IssuedAt = now,
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var noJtiJwt = tokenHandler.WriteToken(token);

                // Act
                var result = await service.ValidateAccessTokenWithTypeAsync(noJtiJwt);

                // Assert
                Assert.False(result.IsValid);
            }
        }

        [Fact]
        public async Task GenerateAccessTokenAsync_ShouldSaveTokenToDatabase()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger, _httpContextAccessor);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "email" }
            };

            // Act
            var accessToken = await service.GenerateAccessTokenAsync(request);

            // JWT から jti を取得
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);
            var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

            // Assert - DB には jti が保存されている
            var savedToken = await context.AccessTokens.FirstOrDefaultAsync(at => at.Token == jti);
            Assert.NotNull(savedToken);
            Assert.Equal(user.Subject, savedToken.Subject);
            Assert.Equal(client.Id, savedToken.ClientId);
            Assert.Equal("openid email", savedToken.Scopes);
            Assert.False(savedToken.IsExpired);
            Assert.False(savedToken.IsRevoked);
            Assert.True(savedToken.ExpiresAt > DateTime.UtcNow);
        }
    }
}
