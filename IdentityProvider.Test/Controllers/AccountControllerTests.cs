using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class AccountControllerTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly Mock<ITokenService> _mockTokenService;
        private readonly Mock<IAccountService> _mockAccountService;
        private readonly AccountController _controller;

        private const string AccountToken = "account-access-token";
        private const string AccountSubject = "account-subject-1";

        public AccountControllerTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _mockTokenService = new Mock<ITokenService>();
            _mockAccountService = new Mock<IAccountService>();

            _controller = new AccountController(
                _context,
                _mockTokenService.Object,
                _mockAccountService.Object,
                new PlaintextSecretProtector(),
                new Mock<ILogger<AccountController>>().Object);
        }

        private void SetBearer(string? token)
        {
            var httpContext = new DefaultHttpContext();
            if (token != null)
            {
                httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
            }
            _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        }

        private void SetupValidAccountToken()
        {
            _mockTokenService
                .Setup(x => x.ValidateAccessTokenWithTypeAsync(AccountToken))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = AccountSubject,
                    SubjectType = SubjectType.Account
                });
        }

        private async Task SeedOrgWithClient(int orgId, string code, bool isSandbox, int clientDbId, string clientId, string secret)
        {
            _context.Organizations.Add(new Organization
            {
                Id = orgId,
                Code = code,
                Name = code + " Shop",
                TenantName = code,
                IsSandbox = isSandbox,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            _context.Clients.Add(new Client
            {
                Id = clientDbId,
                ClientId = clientId,
                ClientSecret = secret, // PlaintextSecretProtector 使用のため平文パススルー
                AppName = code + " App",
                OrganizationId = orgId
            });
            await _context.SaveChangesAsync();
        }

        [Fact]
        public async Task GetClients_ValidAccountToken_ReturnsManagedClientsOnly()
        {
            // Arrange: org1(本番)/org2(テスト) は管理対象、org3 は非管理
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret-prod");
            await SeedOrgWithClient(2, "shop1-test", true, 20, "client-test", "secret-test");
            await SeedOrgWithClient(3, "other", false, 30, "client-other", "secret-other");

            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization>
                {
                    new(1, "shop1", "owner"),
                    new(2, "shop1-test", "owner")
                });
            SetupValidAccountToken();
            SetBearer(AccountToken);

            // Act
            var result = await _controller.GetClients();

            // Assert
            var ok = Assert.IsType<OkObjectResult>(result);
            var clients = GetClientList(ok.Value);
            Assert.Equal(2, clients.Count);
            var clientIds = clients.Select(c => (string)GetProp(c, "client_id")).ToHashSet();
            Assert.Contains("client-prod", clientIds);
            Assert.Contains("client-test", clientIds);
            Assert.DoesNotContain("client-other", clientIds);
            // 一覧では client_secret の値を返さず、設定済みかどうかのみ返す
            var prod = clients.First(c => (string)GetProp(c, "client_id") == "client-prod");
            Assert.Null(prod.GetType().GetProperty("client_secret"));
            Assert.True((bool)GetProp(prod, "has_secret"));
            Assert.False((bool)GetProp(prod, "is_sandbox"));
        }

        [Fact]
        public async Task GetClients_NoToken_ReturnsUnauthorized()
        {
            SetBearer(null);
            var result = await _controller.GetClients();
            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        [Fact]
        public async Task GetClients_NonAccountToken_ReturnsUnauthorized()
        {
            _mockTokenService
                .Setup(x => x.ValidateAccessTokenWithTypeAsync("b2b-token"))
                .ReturnsAsync(new ITokenService.AccessTokenValidationResult
                {
                    IsValid = true,
                    Subject = "b2b-subject",
                    SubjectType = SubjectType.B2B
                });
            SetBearer("b2b-token");

            var result = await _controller.GetClients();
            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        [Fact]
        public async Task RegenerateSecret_OwnedClient_RotatesSecret()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "old-secret");
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization> { new(1, "shop1", "owner") });
            SetupValidAccountToken();
            SetBearer(AccountToken);

            var result = await _controller.RegenerateSecret(10);

            var ok = Assert.IsType<OkObjectResult>(result);
            var newSecret = (string)GetProp(ok.Value!, "client_secret");
            Assert.False(string.IsNullOrEmpty(newSecret));
            Assert.NotEqual("old-secret", newSecret);
            // DB 上も更新されている（PlaintextSecretProtector のため平文一致）
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 10);
            Assert.Equal(newSecret, stored.ClientSecret);
        }

        [Fact]
        public async Task RegenerateSecret_NotOwnedClient_ReturnsNotFound()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "old-secret");
            await SeedOrgWithClient(3, "other", false, 30, "client-other", "other-secret");
            // Account は org1 のみ管理
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization> { new(1, "shop1", "owner") });
            SetupValidAccountToken();
            SetBearer(AccountToken);

            // 非管理の client 30 の secret 再生成を試みる
            var result = await _controller.RegenerateSecret(30);

            Assert.IsType<NotFoundObjectResult>(result);
            // 変更されていない
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 30);
            Assert.Equal("other-secret", stored.ClientSecret);
        }

        [Fact]
        public async Task RevealSecret_OwnedClient_ReturnsPlaintextSecret()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret-prod");
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization> { new(1, "shop1", "owner") });
            SetupValidAccountToken();
            SetBearer(AccountToken);

            var result = await _controller.RevealSecret(10);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("client-prod", (string)GetProp(ok.Value!, "client_id"));
            Assert.Equal("secret-prod", (string)GetProp(ok.Value!, "client_secret"));
        }

        [Fact]
        public async Task RevealSecret_NotOwnedClient_ReturnsNotFound()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret-prod");
            await SeedOrgWithClient(3, "other", false, 30, "client-other", "other-secret");
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization> { new(1, "shop1", "owner") });
            SetupValidAccountToken();
            SetBearer(AccountToken);

            var result = await _controller.RevealSecret(30);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task RevealSecret_NoToken_ReturnsUnauthorized()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret-prod");
            SetBearer(null);

            var result = await _controller.RevealSecret(10);

            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        // 匿名オブジェクトのプロパティをリフレクションで取り出すヘルパー
        private static object GetProp(object obj, string name) =>
            obj.GetType().GetProperty(name)!.GetValue(obj)!;

        private static List<object> GetClientList(object? okValue)
        {
            var clients = okValue!.GetType().GetProperty("clients")!.GetValue(okValue)!;
            return ((IEnumerable<object>)clients).ToList();
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
