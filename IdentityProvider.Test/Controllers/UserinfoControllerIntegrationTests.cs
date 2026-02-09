using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class UserinfoControllerIntegrationTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly ITokenService _tokenService;
        private readonly IUserService _userService;
        private readonly Mock<IB2BUserService> _mockB2BUserService;
        private readonly Mock<ILogger<UserinfoController>> _mockLogger;
        private readonly UserinfoController _controller;
        private readonly MockTenantService _mockTenantService;

        public UserinfoControllerIntegrationTests()
        {
            _mockTenantService = new MockTenantService();
            _context = TestDbContextHelper.CreateInMemoryContext(tenantService: _mockTenantService);

            // 実際のサービスのインスタンスを作成（統合テスト）
            var mockLogger = new Mock<ILogger<TokenService>>();
            var mockUserLogger = new Mock<ILogger<UserService>>();

            _tokenService = new TokenService(_context, mockLogger.Object);
            _userService = new UserService(_context, mockUserLogger.Object);
            _mockB2BUserService = new Mock<IB2BUserService>();

            _mockLogger = new Mock<ILogger<UserinfoController>>();

            _controller = new UserinfoController(
                _tokenService,
                _userService,
                _mockB2BUserService.Object,
                _mockLogger.Object);

            // HttpContext のセットアップ
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Fact]
        public async Task IntegrationTest_FullWorkflow_ValidAccessToken_ReturnsUserInfo()
        {
            // Arrange
            var (client, rsaKeyPair) = await SeedTestDataAsync();

            var user = await _context.EcAuthUsers.FirstAsync(u => u.Subject == "integration-test-subject");
            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal("integration-test-subject", subProperty);
        }

        [Fact]
        public async Task IntegrationTest_ExpiredAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var (client, rsaKeyPair) = await SeedTestDataAsync();

            // 期限切れの JWT を手動生成
            var jti = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            string expiredJwt;
            using (var rsa = RSA.Create())
            {
                rsa.ImportRSAPrivateKey(Convert.FromBase64String(rsaKeyPair.PrivateKey), out _);

                var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                };

                var claims = new List<Claim>
                {
                    new(JwtRegisteredClaimNames.Sub, "integration-test-subject"),
                    new("sub_type", "b2c"),
                    new("org_id", "1", ClaimValueTypes.Integer32),
                    new("client_id", client.ClientId),
                    new(JwtRegisteredClaimNames.Iss, "https://ecauth.example.com"),
                    new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now.AddHours(-2)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Exp, new DateTimeOffset(now.AddHours(-1)).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                    new(JwtRegisteredClaimNames.Jti, jti)
                };

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = now.AddHours(-1),
                    NotBefore = now.AddHours(-2),
                    IssuedAt = now.AddHours(-2),
                    SigningCredentials = signingCredentials
                };

                var tokenHandler = new JwtSecurityTokenHandler();
                var token = tokenHandler.CreateToken(tokenDescriptor);
                expiredJwt = tokenHandler.WriteToken(token);
            }

            // DB にメタデータを保存
            _context.AccessTokens.Add(new AccessToken
            {
                Token = jti,
                ExpiresAt = now.AddHours(-1),
                ClientId = client.Id,
                Subject = "integration-test-subject",
                SubjectType = SubjectType.B2C,
                CreatedAt = now.AddHours(-2),
                Scopes = "openid profile"
            });
            await _context.SaveChangesAsync();

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {expiredJwt}";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task IntegrationTest_MultiTenant_CorrectUserForTenant()
        {
            // Arrange
            var (client1, rsaKeyPair1) = await SeedMultiTenantDataAsync();

            // テナント1に切り替え
            _mockTenantService.SetTenant("tenant1");

            var user = await _context.EcAuthUsers.FirstAsync(u => u.Subject == "tenant1-user-subject");
            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client1,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal("tenant1-user-subject", subProperty);
        }

        [Fact]
        public async Task IntegrationTest_CrossTenantAccess_ReturnsUnauthorized()
        {
            // Arrange
            var (client1, rsaKeyPair1) = await SeedMultiTenantDataAsync();

            // テナント1のユーザー用のトークンを生成
            _mockTenantService.SetTenant("tenant1");
            var user = await _context.EcAuthUsers.FirstAsync(u => u.Subject == "tenant1-user-subject");
            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client1,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            // テナント2に切り替え（クロステナントアクセス）
            _mockTenantService.SetTenant("tenant2");

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task IntegrationTest_RevokedAccessToken_ReturnsUnauthorized()
        {
            // Arrange
            var (client, rsaKeyPair) = await SeedTestDataAsync();

            var user = await _context.EcAuthUsers.FirstAsync(u => u.Subject == "integration-test-subject");
            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            // トークンを無効化
            await _tokenService.RevokeAccessTokenAsync(accessToken);

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            var response = unauthorizedResult.Value;
            Assert.NotNull(response);

            var errorProperty = response.GetType().GetProperty("error")?.GetValue(response);
            Assert.Equal("invalid_token", errorProperty);
        }

        [Fact]
        public async Task IntegrationTest_B2BAccessToken_SubjectTypeB2B_ReturnsUserInfo()
        {
            // Arrange
            var (client, rsaKeyPair) = await SeedB2BTestDataAsync();

            var b2bUser = await _context.B2BUsers.FirstAsync(u => u.Subject == "b2b-integration-test-subject");
            var request = new ITokenService.TokenRequest
            {
                User = b2bUser,
                Client = client,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2B
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            // B2BUserServiceのモック設定
            _mockB2BUserService.Setup(x => x.GetBySubjectAsync("b2b-integration-test-subject"))
                .ReturnsAsync(b2bUser);

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal("b2b-integration-test-subject", subProperty);
        }

        [Fact]
        public async Task IntegrationTest_B2CAccessToken_SubjectTypeB2C_ReturnsUserInfo()
        {
            // Arrange
            var (client, rsaKeyPair) = await SeedTestDataAsync();

            var user = await _context.EcAuthUsers.FirstAsync(u => u.Subject == "integration-test-subject");
            var request = new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid", "profile" },
                SubjectType = SubjectType.B2C
            };
            var accessToken = await _tokenService.GenerateAccessTokenAsync(request);

            _controller.HttpContext.Request.Headers["Authorization"] = $"Bearer {accessToken}";

            // Act
            var result = await _controller.Get();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            var subProperty = response.GetType().GetProperty("sub")?.GetValue(response);
            Assert.Equal("integration-test-subject", subProperty);
        }

        private async Task<(Client client, RsaKeyPair rsaKeyPair)> SeedTestDataAsync()
        {
            var organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "Test Organization",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "integration-test-subject",
                EmailHash = "integration-test-email-hash",
                OrganizationId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.EcAuthUsers.Add(user);

            var rsaKeyPair = TestDbContextHelper.GenerateAndAddRsaKeyPair(_context, client, 1);

            await _context.SaveChangesAsync();

            return (client, rsaKeyPair);
        }

        private async Task<(Client client, RsaKeyPair rsaKeyPair)> SeedB2BTestDataAsync()
        {
            var organization = new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "Test Organization",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var b2bUser = new B2BUser
            {
                Subject = "b2b-integration-test-subject",
                ExternalId = "b2b-admin@example.com",
                UserType = "admin",
                OrganizationId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BUsers.Add(b2bUser);

            var rsaKeyPair = TestDbContextHelper.GenerateAndAddRsaKeyPair(_context, client, 1);

            await _context.SaveChangesAsync();

            return (client, rsaKeyPair);
        }

        private async Task<(Client client1, RsaKeyPair rsaKeyPair1)> SeedMultiTenantDataAsync()
        {
            // テナント1
            var org1 = new Organization
            {
                Id = 1,
                Code = "tenant1-org",
                Name = "Tenant 1 Organization",
                TenantName = "tenant1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(org1);

            var client1 = new Client
            {
                Id = 1,
                ClientId = "tenant1-client",
                ClientSecret = "tenant1-secret",
                AppName = "Tenant 1 App",
                OrganizationId = 1
            };
            _context.Clients.Add(client1);

            var user1 = new EcAuthUser
            {
                Subject = "tenant1-user-subject",
                EmailHash = "tenant1-email-hash",
                OrganizationId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.EcAuthUsers.Add(user1);

            var rsaKeyPair1 = TestDbContextHelper.GenerateAndAddRsaKeyPair(_context, client1, 1);

            // テナント2
            var org2 = new Organization
            {
                Id = 2,
                Code = "tenant2-org",
                Name = "Tenant 2 Organization",
                TenantName = "tenant2",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(org2);

            var client2 = new Client
            {
                Id = 2,
                ClientId = "tenant2-client",
                ClientSecret = "tenant2-secret",
                AppName = "Tenant 2 App",
                OrganizationId = 2
            };
            _context.Clients.Add(client2);

            var user2 = new EcAuthUser
            {
                Subject = "tenant2-user-subject",
                EmailHash = "tenant2-email-hash",
                OrganizationId = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.EcAuthUsers.Add(user2);

            TestDbContextHelper.GenerateAndAddRsaKeyPair(_context, client2, 2);

            await _context.SaveChangesAsync();

            return (client1, rsaKeyPair1);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
