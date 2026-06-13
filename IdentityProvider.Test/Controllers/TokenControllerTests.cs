using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class TokenControllerTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly Mock<IHostEnvironment> _mockEnvironment;
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<IUserService> _mockUserService;
        private readonly Mock<IB2BUserService> _mockB2BUserService;
        private readonly Mock<IAccountService> _mockAccountService;
        private readonly Mock<ILogger<TokenController>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly TokenController _controller;

        public TokenControllerTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockEnvironment = new Mock<IHostEnvironment>();
            _mockTokenService = new Mock<ITokenService>();
            _mockUserService = new Mock<IUserService>();
            _mockB2BUserService = new Mock<IB2BUserService>();
            _mockAccountService = new Mock<IAccountService>();
            _mockLogger = new Mock<ILogger<TokenController>>();
            _mockConfiguration = new Mock<IConfiguration>();

            _controller = new TokenController(
                _context,
                _mockEnvironment.Object,
                _mockTokenService.Object,
                _mockUserService.Object,
                _mockB2BUserService.Object,
                _mockAccountService.Object,
                _mockLogger.Object,
                _mockConfiguration.Object);
        }

        [Fact]
        public async Task Token_ValidAuthorizationCode_ReturnsTokens()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };
            _context.EcAuthUsers.Add(user);

            var authCode = new AuthorizationCode
            {
                Code = "test-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            var expectedTokenResponse = new ITokenService.TokenResponse
            {
                AccessToken = "access-token",
                IdToken = "id-token",
                ExpiresIn = 3600,
                TokenType = "Bearer",
                RefreshToken = "refresh-token"
            };

            _mockUserService.Setup(x => x.GetUserBySubjectAsync("test-subject"))
                .ReturnsAsync(user);

            _mockTokenService.Setup(x => x.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()))
                .ReturnsAsync(expectedTokenResponse);

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://example.com/callback",
                "1",
                "test-secret");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var response = okResult.Value;

            Assert.NotNull(response);
            
            // 認可コードが使用済みになっていることを確認
            var updatedAuthCode = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "test-code");
            Assert.True(updatedAuthCode.IsUsed);
            Assert.NotNull(updatedAuthCode.UsedAt);
        }

        [Fact]
        public async Task Token_AccountAuthorizationCode_ReturnsTokensWithManagedOrgs()
        {
            // Arrange
            var organization = new Organization
            {
                Id = 1,
                Code = "accounts",
                Name = "EcAuth Accounts",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "ecauth-admin-console",
                ClientSecret = "account-secret",
                AppName = "EcAuth Admin Console",
                OrganizationId = 1,
                SubjectType = SubjectType.Account
            };
            _context.Clients.Add(client);

            var authCode = new AuthorizationCode
            {
                Code = "account-code",
                Subject = "account-subject",
                SubjectType = SubjectType.Account,
                ClientId = 1,
                RedirectUri = "https://accounts.ec-auth.io/callback",
                Scope = "openid b2b",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            var account = new Account
            {
                Id = 1,
                Subject = "account-subject",
                Email = "owner@example.com",
                OrganizationId = 1
            };
            var managedOrgs = new List<IAccountService.ManagedOrganization>
            {
                new(123, "customer-shop", "owner")
            };

            _mockAccountService.Setup(x => x.GetBySubjectAsync("account-subject"))
                .ReturnsAsync(account);
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync("account-subject"))
                .ReturnsAsync(managedOrgs);

            ITokenService.TokenRequest? capturedRequest = null;
            _mockTokenService.Setup(x => x.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()))
                .Callback<ITokenService.TokenRequest>(r => capturedRequest = r)
                .ReturnsAsync(new ITokenService.TokenResponse
                {
                    AccessToken = "access-token",
                    IdToken = "id-token",
                    ExpiresIn = 3600,
                    TokenType = "Bearer"
                });

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "account-code",
                "https://accounts.ec-auth.io/callback",
                "ecauth-admin-console",
                "account-secret");

            // Assert
            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(capturedRequest);
            Assert.Equal(SubjectType.Account, capturedRequest!.SubjectType);
            Assert.Same(account, capturedRequest.User);
            Assert.NotNull(capturedRequest.ManagedOrgs);
            Assert.Single(capturedRequest.ManagedOrgs!);

            var updatedAuthCode = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "account-code");
            Assert.True(updatedAuthCode.IsUsed);
        }

        [Fact]
        public async Task Token_AccountNotFound_ReturnsBadRequest()
        {
            // Arrange
            var organization = new Organization
            {
                Id = 1,
                Code = "accounts",
                Name = "EcAuth Accounts",
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "ecauth-admin-console",
                ClientSecret = "account-secret",
                AppName = "EcAuth Admin Console",
                OrganizationId = 1,
                SubjectType = SubjectType.Account
            };
            _context.Clients.Add(client);

            var authCode = new AuthorizationCode
            {
                Code = "account-code",
                Subject = "missing-account-subject",
                SubjectType = SubjectType.Account,
                ClientId = 1,
                RedirectUri = "https://accounts.ec-auth.io/callback",
                Scope = "openid b2b",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            _mockAccountService.Setup(x => x.GetBySubjectAsync("missing-account-subject"))
                .ReturnsAsync((Account?)null);

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "account-code",
                "https://accounts.ec-auth.io/callback",
                "ecauth-admin-console",
                "account-secret");

            // Assert
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Token_InvalidGrantType_ReturnsBadRequest()
        {
            // Act
            var result = await _controller.Token(
                "invalid_grant_type",
                "test-code",
                "https://example.com/callback",
                "1",
                null);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_ClientNotFound_ReturnsBadRequest()
        {
            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://example.com/callback",
                "999",
                null);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_InvalidClientSecret_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "correct-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://example.com/callback",
                "1",
                "wrong-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_AuthorizationCodeNotFound_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "nonexistent-code",
                "https://example.com/callback",
                "1",
                "test-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_ExpiredAuthorizationCode_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };
            _context.EcAuthUsers.Add(user);

            var authCode = new AuthorizationCode
            {
                Code = "expired-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10), // 期限切れ
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "expired-code",
                "https://example.com/callback",
                "1",
                "test-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_UsedAuthorizationCode_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };
            _context.EcAuthUsers.Add(user);

            var authCode = new AuthorizationCode
            {
                Code = "used-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = true, // 使用済み
                UsedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "used-code",
                "https://example.com/callback",
                "1",
                "test-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_RedirectUriMismatch_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };
            _context.EcAuthUsers.Add(user);

            var authCode = new AuthorizationCode
            {
                Code = "test-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://original.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://different.com/callback", // 異なるリダイレクトURI
                "1",
                "test-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_ClientIdMismatch_ReturnsBadRequest()
        {
            // Arrange
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

            var client1 = new Client
            {
                Id = 1,
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App 1",
                OrganizationId = 1
            };
            _context.Clients.Add(client1);

            var client2 = new Client
            {
                Id = 2,
                ClientId = "2",
                ClientSecret = "test-secret-2",
                AppName = "Test App 2",
                OrganizationId = 1
            };
            _context.Clients.Add(client2);

            var user = new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            };
            _context.EcAuthUsers.Add(user);

            var authCode = new AuthorizationCode
            {
                Code = "test-code",
                Subject = "test-subject",
                ClientId = 1, // client1用のコード
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            // Act - client2でアクセス
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://example.com/callback",
                "2", // 異なるクライアントID
                "test-secret-2");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Token_UserNotFound_ReturnsBadRequest()
        {
            // Arrange
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
                ClientId = "1",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            };
            _context.Clients.Add(client);

            var authCode = new AuthorizationCode
            {
                Code = "test-code",
                Subject = "nonexistent-subject",
                ClientId = 1,
                RedirectUri = "https://example.com/callback",
                Scope = "openid profile",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            };
            _context.AuthorizationCodes.Add(authCode);
            await _context.SaveChangesAsync();

            _mockUserService.Setup(x => x.GetUserBySubjectAsync("nonexistent-subject"))
                .ReturnsAsync((EcAuthUser?)null);

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://example.com/callback",
                "1",
                "test-secret");

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}