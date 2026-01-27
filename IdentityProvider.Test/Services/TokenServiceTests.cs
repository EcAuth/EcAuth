using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace IdentityProvider.Test.Services
{
    public class TokenServiceTests
    {
        private readonly ILogger<TokenService> _logger;

        public TokenServiceTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<TokenService>();
        }

        [Fact]
        public async Task GenerateIdTokenAsync_ValidRequest_ShouldGenerateValidJwtToken()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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
            Assert.Equal("https://ecauth.example.com", jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value);
            Assert.Equal(client.ClientId, jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Aud)?.Value);
            Assert.Equal("test-nonce", jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Nonce)?.Value);
            Assert.NotNull(jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value);
            Assert.Equal("true", jsonToken.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value);
        }

        [Fact]
        public async Task GenerateIdTokenAsync_WithoutNonce_ShouldGenerateTokenWithoutNonceClaim()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            Assert.Equal(64, accessToken.Length); // 32 bytes -> 64 hex chars
        }

        [Fact]
        public async Task GenerateTokensAsync_ValidRequest_ShouldGenerateBothTokens()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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

            // Validate ID token
            var handler = new JwtSecurityTokenHandler();
            var jsonToken = handler.ReadJwtToken(response.IdToken);
            Assert.Equal(user.Subject, jsonToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value);
        }

        [Fact]
        public async Task ValidateTokenAsync_ValidToken_ShouldReturnSubject()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

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

            // Generate RSA key pair for testing
            string publicKey, privateKey;
            using (var rsa = RSA.Create(2048))
            {
                publicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey());
                privateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
            }

            var rsaKeyPair = new RsaKeyPair
            {
                Id = 1,
                ClientId = client.Id,
                PublicKey = publicKey,
                PrivateKey = privateKey,
                Client = client
            };
            context.RsaKeyPairs.Add(rsaKeyPair);

            await context.SaveChangesAsync();

            return (client, user, rsaKeyPair);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_ValidToken_ShouldReturnSubject()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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
            var service = new TokenService(context, _logger);

            // Act
            var subject = await service.ValidateAccessTokenAsync("invalid-token");

            // Assert
            Assert.Null(subject);
        }

        [Fact]
        public async Task ValidateAccessTokenAsync_ExpiredToken_ShouldReturnNull()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            // Create expired access token
            var expiredToken = new AccessToken
            {
                Token = "expired-token",
                ExpiresAt = DateTime.UtcNow.AddHours(-1), // Expired 1 hour ago
                ClientId = client.Id,
                Subject = user.Subject,
                CreatedAt = DateTime.UtcNow.AddHours(-2)
            };
            context.AccessTokens.Add(expiredToken);
            await context.SaveChangesAsync();

            // Act
            var subject = await service.ValidateAccessTokenAsync("expired-token");

            // Assert
            Assert.Null(subject);
        }

        [Fact]
        public async Task RevokeAccessTokenAsync_ValidToken_ShouldRevokeSuccessfully()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

            // Arrange
            var (client, user, _) = await SetupTestDataAsync(context);

            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client
            };

            var accessToken = await service.GenerateAccessTokenAsync(request);

            // Act
            var result = await service.RevokeAccessTokenAsync(accessToken);

            // Assert
            Assert.True(result);

            // Verify token is removed from database
            var removedToken = await context.AccessTokens.FirstOrDefaultAsync(at => at.Token == accessToken);
            Assert.Null(removedToken);
        }

        [Fact]
        public async Task RevokeAccessTokenAsync_InvalidToken_ShouldReturnFalse()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

            // Act
            var result = await service.RevokeAccessTokenAsync("invalid-token");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task GenerateAccessTokenAsync_ShouldSaveTokenToDatabase()
        {
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var service = new TokenService(context, _logger);

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

            // Assert
            var savedToken = await context.AccessTokens.FirstOrDefaultAsync(at => at.Token == accessToken);
            Assert.NotNull(savedToken);
            Assert.Equal(user.Subject, savedToken.Subject);
            Assert.Equal(client.Id, savedToken.ClientId);
            Assert.Equal("openid email", savedToken.Scopes);
            Assert.False(savedToken.IsExpired);
            Assert.True(savedToken.ExpiresAt > DateTime.UtcNow);
        }
    }
}