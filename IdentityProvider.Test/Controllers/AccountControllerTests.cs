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

        private async Task SeedOrgWithClient(
            int orgId, string code, bool isSandbox, int clientDbId, string clientId, string secret,
            string[]? redirectUris = null, string[]? allowedRpIds = null)
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
                OrganizationId = orgId,
                AllowedRpIds = (allowedRpIds ?? Array.Empty<string>()).ToList()
            });
            foreach (var uri in redirectUris ?? Array.Empty<string>())
            {
                _context.RedirectUris.Add(new RedirectUri { ClientId = clientDbId, Uri = uri });
            }
            await _context.SaveChangesAsync();
        }

        /// <summary>管理対象 Organization を 1 件だけ持つ Account として認証済みの状態にする。</summary>
        private void AuthenticateAsOwnerOf(params (int OrgId, string Code)[] orgs)
        {
            _mockAccountService.Setup(x => x.GetManagedOrganizationsAsync(AccountSubject))
                .ReturnsAsync(orgs
                    .Select(o => new IAccountService.ManagedOrganization(o.OrgId, o.Code, "owner"))
                    .ToList());
            SetupValidAccountToken();
            SetBearer(AccountToken);
        }

        private async Task<List<string>> StoredRedirectUris(int clientDbId) =>
            await _context.RedirectUris
                .IgnoreQueryFilters()
                .Where(r => r.ClientId == clientDbId)
                .Select(r => r.Uri)
                .ToListAsync();

        [Fact]
        public async Task GetClients_ValidAccountToken_ReturnsManagedClientsOnly()
        {
            // Arrange: org1(本番)/org2(テスト) は管理対象、org3 は非管理
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret-prod",
                redirectUris: new[] { "https://shop1.example.jp/ecauth/callback" },
                allowedRpIds: new[] { "shop1.example.jp" });
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
            // マイページの編集 UI が現在値を出せるよう、一覧に allowed_rp_ids も含める
            Assert.Equal(new[] { "https://shop1.example.jp/ecauth/callback" }, (string[])GetProp(prod, "redirect_uris"));
            Assert.Equal(new[] { "shop1.example.jp" }, (string[])GetProp(prod, "allowed_rp_ids"));
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

        // ---- redirect_uris の更新 ----

        [Fact]
        public async Task UpdateRedirectUris_OwnedClient_ReplacesWholeList()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                redirectUris: new[] { "https://shop1.example.jp/", "https://shop1.example.jp/old" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateRedirectUris(10, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string> { "https://shop1.example.jp/ecauth/callback" }
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            var returned = (string[])GetProp(ok.Value!, "redirect_uris");
            Assert.Equal(new[] { "https://shop1.example.jp/ecauth/callback" }, returned);
            // 古い 2 件は残らない（追加ではなく全置換）
            Assert.Equal(new[] { "https://shop1.example.jp/ecauth/callback" }, await StoredRedirectUris(10));
        }

        [Fact]
        public async Task UpdateRedirectUris_NormalizesAuthorityButKeepsPathCase()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateRedirectUris(10, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string>
                {
                    // ホストは大文字小文字を区別しないので畳む。IDN は Punycode（ブラウザが送る Host と揃える）。
                    "https://SHOP1.Example.JP/EcAuth/Callback",
                    "https://日本語.example.jp:8443/ecauth/callback",
                    // 既定ポートの明示は authority から落ちる
                    "https://shop1.example.jp:443/EcAuth/Callback"
                }
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            var returned = (string[])GetProp(ok.Value!, "redirect_uris");
            // 1 件目と 3 件目は正規化後に同一になるので重複排除される。パスの大文字は保つ
            // （authenticate/verify は序数完全一致で比較するため、勝手に畳むと一致しなくなる）。
            Assert.Equal(new[]
            {
                "https://shop1.example.jp/EcAuth/Callback",
                "https://xn--wgv71a119e.example.jp:8443/ecauth/callback"
            }, returned);
        }

        [Theory]
        [InlineData("http://shop1.example.jp/ecauth/callback")] // https 必須
        [InlineData("https://shop1.example.jp/callback#frag")]  // フラグメント禁止（RFC 6749 3.1.2）
        [InlineData("https://user:pass@shop1.example.jp/cb")]   // userinfo 禁止
        [InlineData("/ecauth/callback")]                        // 相対 URL
        [InlineData("not a url")]
        public async Task UpdateRedirectUris_InvalidUri_Returns422AndKeepsExisting(string uri)
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                redirectUris: new[] { "https://shop1.example.jp/ecauth/callback" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateRedirectUris(10, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string> { uri }
            });

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal("invalid_redirect_uri", (string)GetProp(unprocessable.Value!, "error"));
            Assert.Equal("redirect_uris", (string)GetProp(unprocessable.Value!, "field"));
            // 既存値は壊さない
            Assert.Equal(new[] { "https://shop1.example.jp/ecauth/callback" }, await StoredRedirectUris(10));
        }

        [Fact]
        public async Task UpdateRedirectUris_EmptyList_Returns422()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                redirectUris: new[] { "https://shop1.example.jp/ecauth/callback" });
            AuthenticateAsOwnerOf((1, "shop1"));

            // 空文字だけのリストは「実質空」として扱い、認証不能な状態への更新を拒否する
            var result = await _controller.UpdateRedirectUris(10, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string> { "", "   " }
            });

            Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Single(await StoredRedirectUris(10));
        }

        [Fact]
        public async Task UpdateRedirectUris_NullBody_Returns422()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateRedirectUris(10, null);

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal("invalid_request", (string)GetProp(unprocessable.Value!, "error"));
        }

        [Fact]
        public async Task UpdateRedirectUris_NotOwnedClient_ReturnsNotFound()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            await SeedOrgWithClient(3, "other", false, 30, "client-other", "secret",
                redirectUris: new[] { "https://other.example.jp/ecauth/callback" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateRedirectUris(30, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string> { "https://attacker.example.com/steal" }
            });

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(new[] { "https://other.example.jp/ecauth/callback" }, await StoredRedirectUris(30));
        }

        [Fact]
        public async Task UpdateRedirectUris_NoToken_ReturnsUnauthorized()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            SetBearer(null);

            var result = await _controller.UpdateRedirectUris(10, new AccountController.RedirectUrisDto
            {
                RedirectUris = new List<string> { "https://shop1.example.jp/ecauth/callback" }
            });

            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        // ---- allowed_rp_ids の更新 ----

        [Fact]
        public async Task UpdateAllowedRpIds_OwnedClient_ReplacesWholeListAndPersists()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                allowedRpIds: new[] { "old.example.jp" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string> { "shop1.example.jp", "www.shop1.example.jp" }
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(new[] { "shop1.example.jp", "www.shop1.example.jp" },
                (string[])GetProp(ok.Value!, "allowed_rp_ids"));

            // AllowedRpIds の getter は毎回新しいリストを返すため、リストごと再代入しないと
            // AllowedRpIdsJson に反映されない。DB から読み直して永続化を確認する。
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 10);
            Assert.Equal(new[] { "shop1.example.jp", "www.shop1.example.jp" }, stored.AllowedRpIds);
            Assert.DoesNotContain("old.example.jp", stored.AllowedRpIds);
        }

        [Fact]
        public async Task UpdateAllowedRpIds_NormalizesCaseIdnAndDeduplicates()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string> { "Shop1.Example.JP", "shop1.example.jp", "日本語.example.jp", "localhost" }
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(
                new[] { "shop1.example.jp", "xn--wgv71a119e.example.jp", "localhost" },
                (string[])GetProp(ok.Value!, "allowed_rp_ids"));
        }

        [Theory]
        [InlineData("https://shop1.example.jp")] // スキーム付き
        [InlineData("shop1.example.jp:443")]     // ポート付き
        [InlineData("shop1.example.jp/admin")]   // パス付き
        [InlineData("192.0.2.10")]               // IPv4
        [InlineData("[2001:db8::1]")]            // IPv6
        [InlineData("shop1..example.jp")]        // 空ラベル
        [InlineData("shop1.example.jp.")]        // 末尾ドット（FQDN 表記）
        public async Task UpdateAllowedRpIds_InvalidRpId_Returns422AndKeepsExisting(string rpId)
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                allowedRpIds: new[] { "shop1.example.jp" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string> { rpId }
            });

            var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.Equal("invalid_rp_id", (string)GetProp(unprocessable.Value!, "error"));
            Assert.Equal("allowed_rp_ids", (string)GetProp(unprocessable.Value!, "field"));

            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 10);
            Assert.Equal(new[] { "shop1.example.jp" }, stored.AllowedRpIds);
        }

        [Fact]
        public async Task UpdateAllowedRpIds_EmptyList_Returns422()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                allowedRpIds: new[] { "shop1.example.jp" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string>()
            });

            Assert.IsType<UnprocessableEntityObjectResult>(result);
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 10);
            Assert.Equal(new[] { "shop1.example.jp" }, stored.AllowedRpIds);
        }

        [Fact]
        public async Task UpdateAllowedRpIds_TooLongForColumn_Returns422()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret",
                allowedRpIds: new[] { "shop1.example.jp" });
            AuthenticateAsOwnerOf((1, "shop1"));

            // allowed_rp_ids カラムは MaxLength(2000)。件数上限（20）以内でも
            // シリアライズ後に溢れうるので、長さ側でも弾けることを確認する。
            // 1 ラベルは DNS の 63 文字上限に収める（超えると Punycode 変換で弾かれ、別の理由で 422 になる）。
            var label = new string('a', 60);
            var rpIds = Enumerable.Range(0, 20).Select(i => $"{label}.{label}.n{i}.example.jp").ToList();

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = rpIds
            });

            Assert.IsType<UnprocessableEntityObjectResult>(result);
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 10);
            Assert.Equal(new[] { "shop1.example.jp" }, stored.AllowedRpIds);
        }

        [Fact]
        public async Task UpdateAllowedRpIds_NotOwnedClient_ReturnsNotFound()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            await SeedOrgWithClient(3, "other", false, 30, "client-other", "secret",
                allowedRpIds: new[] { "other.example.jp" });
            AuthenticateAsOwnerOf((1, "shop1"));

            var result = await _controller.UpdateAllowedRpIds(30, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string> { "attacker.example.com" }
            });

            Assert.IsType<NotFoundObjectResult>(result);
            var stored = await _context.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == 30);
            Assert.Equal(new[] { "other.example.jp" }, stored.AllowedRpIds);
        }

        [Fact]
        public async Task UpdateAllowedRpIds_NoToken_ReturnsUnauthorized()
        {
            await SeedOrgWithClient(1, "shop1", false, 10, "client-prod", "secret");
            SetBearer(null);

            var result = await _controller.UpdateAllowedRpIds(10, new AccountController.AllowedRpIdsDto
            {
                AllowedRpIds = new List<string> { "shop1.example.jp" }
            });

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
