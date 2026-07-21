using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdpUtilities.Security;
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
        // Mock<IConfiguration> は使わない。GetValue<T> は拡張メソッドで GetSection を呼ぶため、
        // loose Mock だと null が返り NullReferenceException になる（PkcePolicy.IsRequired が該当）。
        private readonly IConfiguration _configuration;
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
            _configuration = new ConfigurationBuilder().Build();

            _controller = new TokenController(
                _context,
                _mockEnvironment.Object,
                _mockTokenService.Object,
                _mockUserService.Object,
                _mockB2BUserService.Object,
                _mockAccountService.Object,
                _mockLogger.Object,
                _configuration,
                new PlaintextSecretProtector());
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
                // PKCE 必須化（PkcePolicy）により confidential client でも束縛が必要
                CodeChallenge = PkceChallenge,
                CodeChallengeMethod = "S256",
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
                "test-secret",
                code_verifier: PkceVerifier);

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
                // PKCE 必須化（PkcePolicy）により束縛が必要
                CodeChallenge = PkceChallenge,
                CodeChallengeMethod = "S256",
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
                "account-secret",
                code_verifier: PkceVerifier);

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
                // PKCE を束縛しておく。省くと PKCE 必須化の分岐で BadRequest になり、
                // 本来検証したい「アカウント不在」に到達しないまま緑になってしまう。
                CodeChallenge = PkceChallenge,
                CodeChallengeMethod = "S256",
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
                "account-secret",
                code_verifier: PkceVerifier);

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
                // PKCE を束縛しておく。省くと PKCE 必須化の分岐で BadRequest になり、
                // 本来検証したい「ユーザー不在」に到達しないまま緑になってしまう。
                CodeChallenge = PkceChallenge,
                CodeChallengeMethod = "S256",
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
                "test-secret",
                code_verifier: PkceVerifier);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            var response = badRequestResult.Value;
            Assert.NotNull(response);
        }

        // PKCE (RFC 7636) Appendix B の公式テストベクタ
        private const string PkceVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        private const string PkceChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        private async Task SeedPublicClientWithAuthCode(string? codeChallenge)
        {
            _context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "accounts",
                Name = "EcAuth Accounts",
                // テスト harness の ambient テナント（MockTenantService.TenantName）に一致させる。
                // 一致しないとクエリフィルタで除外され client/認可コードが見つからない。
                TenantName = "test-tenant",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            // public client: client_secret は空
            _context.Clients.Add(new Client
            {
                Id = 1,
                ClientId = "ecauth-admin-console",
                ClientSecret = string.Empty,
                AppName = "EcAuth Admin Console",
                OrganizationId = 1
            });
            _context.EcAuthUsers.Add(new EcAuthUser
            {
                Subject = "test-subject",
                EmailHash = "test-email-hash",
                OrganizationId = 1
            });
            _context.AuthorizationCodes.Add(new AuthorizationCode
            {
                Code = "test-code",
                Subject = "test-subject",
                ClientId = 1,
                RedirectUri = "https://ec-auth.io/auth/callback",
                Scope = "openid profile",
                CodeChallenge = codeChallenge,
                CodeChallengeMethod = codeChallenge == null ? null : "S256",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IsUsed = false
            });
            await _context.SaveChangesAsync();
        }

        [Fact]
        public async Task Token_PublicClientWithValidPkce_ReturnsTokens()
        {
            // Arrange: public client ＋ code_challenge を束縛した認可コード
            await SeedPublicClientWithAuthCode(PkceChallenge);

            _mockUserService.Setup(x => x.GetUserBySubjectAsync("test-subject"))
                .ReturnsAsync(new EcAuthUser { Subject = "test-subject", EmailHash = "h", OrganizationId = 1 });
            _mockTokenService.Setup(x => x.GenerateTokensAsync(It.IsAny<ITokenService.TokenRequest>()))
                .ReturnsAsync(new ITokenService.TokenResponse
                {
                    AccessToken = "access-token",
                    IdToken = "id-token",
                    ExpiresIn = 3600,
                    TokenType = "Bearer",
                    RefreshToken = "refresh-token"
                });

            // Act: 正しい code_verifier を提示
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://ec-auth.io/auth/callback",
                "ecauth-admin-console",
                null,
                PkceVerifier);

            // Assert
            Assert.IsType<OkObjectResult>(result);
            var updated = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "test-code");
            Assert.True(updated.IsUsed);
        }

        [Fact]
        public async Task Token_PublicClientWithoutPkce_ReturnsInvalidGrant()
        {
            // Arrange: public client だが認可コードに code_challenge が無い（PKCE 未使用）
            await SeedPublicClientWithAuthCode(null);

            // Act: public client は PKCE 必須のため拒否されるべき
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://ec-auth.io/auth/callback",
                "ecauth-admin-console",
                null,
                null);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequest.Value);
            // 認可コードは消費されない（マーク前に拒否）
            var authCode = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "test-code");
            Assert.False(authCode.IsUsed);
        }

        [Fact]
        public async Task Token_PkceCodeVerifierMismatch_ReturnsInvalidGrant()
        {
            // Arrange
            await SeedPublicClientWithAuthCode(PkceChallenge);

            // Act: 誤った code_verifier
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://ec-auth.io/auth/callback",
                "ecauth-admin-console",
                null,
                "wrong-verifier-value-0000000000000000000000");

            // Assert
            Assert.IsType<BadRequestObjectResult>(result);
            var authCode = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "test-code");
            Assert.False(authCode.IsUsed);
        }

        [Fact]
        public async Task Token_PkceMissingCodeVerifier_ReturnsInvalidGrant()
        {
            // Arrange: 認可コードに code_challenge があるのに code_verifier 未提示
            await SeedPublicClientWithAuthCode(PkceChallenge);

            // Act
            var result = await _controller.Token(
                "authorization_code",
                "test-code",
                "https://ec-auth.io/auth/callback",
                "ecauth-admin-console",
                null,
                null);

            // Assert
            Assert.IsType<BadRequestObjectResult>(result);
            var authCode = await _context.AuthorizationCodes.FirstAsync(ac => ac.Code == "test-code");
            Assert.False(authCode.IsUsed);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}