using System.Net;
using System.Text;
using System.Web;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    /// <summary>
    /// /v1/auth/callback（B2C フェデレーション）の JIT プロビジョニングと認可コード発行を検証する。
    ///
    /// EcAuthDocs#122 で <see cref="UserService"/> のユーザー解決（ExternalIdpMapping → identity）を
    /// 作り替える前の安全網（EcAuthDocs#164 段 2）。外部 IdP との通信は
    /// <see cref="IHttpClientFactory"/> を偽の <see cref="HttpMessageHandler"/> に差し替えて再現し、
    /// UserService / AuthorizationCodeService は本物を InMemory の context と組み合わせる。
    /// </summary>
    public class AuthorizationCallbackControllerTests : IDisposable
    {
        // Iron はパスワード長 32 文字以上を要求する
        private const string StatePassword = "test-state-password-must-be-32-chars-or-longer";
        private const string ProviderName = "federate-oauth2";
        private const string TokenEndpoint = "https://idp.example.com/token";
        private const string UserinfoEndpoint = "https://idp.example.com/userinfo";
        private const string RedirectUri = "https://client.example.com/callback";
        private const string ValidChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        // Organization の ID は Client / OpenIdProvider の ID と違う値にする（取り違えを検出するため）
        private const int OrganizationId = 10;
        private const int OtherOrganizationId = 20;
        private const int ClientDbId = 1;
        private const int OtherClientDbId = 2;
        private const int ProviderId = 1;
        private const int OtherProviderId = 2;

        private readonly EcAuthDbContext _context;
        private readonly FakeIdpHandler _idp = new();
        private readonly Mock<IExternalIdpTokenService> _externalIdpTokenService = new();
        private readonly IConfiguration _configuration;

        public AuthorizationCallbackControllerTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            Seed();

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEFAULT_ORGANIZATION_REDIRECT_URI"] = "https://localhost:8081/v1/auth/callback",
                    ["STATE_PASSWORD"] = StatePassword
                })
                .Build();
        }

        private void Seed()
        {
            // EcAuthUser のクエリフィルターは Organization.TenantName が MockTenantService（test-tenant）と
            // 一致することを要求するため、両 Organization とも同じテナントに置く。
            _context.Organizations.Add(new Organization { Id = OrganizationId, Code = "org-a", Name = "Org A", TenantName = "test-tenant" });
            _context.Organizations.Add(new Organization { Id = OtherOrganizationId, Code = "org-b", Name = "Org B", TenantName = "test-tenant" });
            _context.Clients.Add(new Client { Id = ClientDbId, ClientId = "client-a", ClientSecret = "secret-a", AppName = "App A", OrganizationId = OrganizationId });
            _context.Clients.Add(new Client { Id = OtherClientDbId, ClientId = "client-b", ClientSecret = "secret-b", AppName = "App B", OrganizationId = OtherOrganizationId });
            // 2 つの Client が同じ名前の OpenIdProvider を持つ（provider.Name が ExternalIdpMapping の
            // ExternalProvider になるため、Organization 境界の検証に使う）
            _context.OpenIdProviders.Add(NewProvider(ProviderId, ClientDbId));
            _context.OpenIdProviders.Add(NewProvider(OtherProviderId, OtherClientDbId));
            _context.SaveChanges();
        }

        private static OpenIdProvider NewProvider(int id, int clientId) => new()
        {
            Id = id,
            Name = ProviderName,
            IdpClientId = "idp-client-id",
            IdpClientSecret = "idp-client-secret",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
            TokenEndpoint = TokenEndpoint,
            UserinfoEndpoint = UserinfoEndpoint,
            ClientId = clientId
        };

        private AuthorizationCallbackController CreateController()
        {
            var factory = new Mock<IHttpClientFactory>();
            factory
                .Setup(f => f.CreateClient(AuthorizationCallbackController.ExternalIdpHttpClientName))
                .Returns(() => new HttpClient(_idp, disposeHandler: false));

            var userService = new UserService(_context, new Mock<ILogger<UserService>>().Object);
            var authorizationCodeService = new TestableAuthorizationCodeService(
                _context, new Mock<ILogger<AuthorizationCodeService>>().Object);

            return new AuthorizationCallbackController(
                _context,
                authorizationCodeService,
                userService,
                _externalIdpTokenService.Object,
                new Mock<ILogger<AuthorizationCallbackController>>().Object,
                factory.Object,
                _configuration);
        }

        private static Task<string> SealState(
            int providerId = ProviderId,
            int clientId = ClientDbId,
            int organizationId = OrganizationId,
            string? clientState = "client-state",
            string? codeChallenge = ValidChallenge)
        {
            var state = new State
            {
                OpenIdProviderId = providerId,
                RedirectUri = RedirectUri,
                ClientId = clientId,
                OrganizationId = organizationId,
                Scope = "openid email profile",
                ClientState = clientState,
                CodeChallenge = codeChallenge,
                CodeChallengeMethod = codeChallenge == null ? null : "S256"
            };
            return Iron.Seal(state, StatePassword, new Iron.Options());
        }

        private async Task<IActionResult> Callback(
            int providerId = ProviderId,
            int clientId = ClientDbId,
            int organizationId = OrganizationId,
            string? clientState = "client-state")
        {
            var state = await SealState(providerId, clientId, organizationId, clientState);
            return await CreateController().Index("idp-code", state, "openid email profile");
        }

        private static string GetCode(IActionResult result)
        {
            var redirect = Assert.IsType<RedirectResult>(result);
            var query = HttpUtility.ParseQueryString(new Uri(redirect.Url).Query);
            var code = query["code"];
            Assert.False(string.IsNullOrEmpty(code));
            return code!;
        }

        // ---- 正常系: JIT プロビジョニングと認可コード発行 ----

        [Fact]
        public async Task Callback_NewUser_CreatesUserAndMappingInStateOrganization()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback();

            GetCode(result);
            var user = Assert.Single(await _context.EcAuthUsers.ToListAsync());
            Assert.Equal(OrganizationId, user.OrganizationId);
            Assert.Equal(EmailHashUtil.HashEmail("user@example.com"), user.EmailHash);
            var mapping = Assert.Single(await _context.ExternalIdpMappings.ToListAsync());
            Assert.Equal(user.Subject, mapping.EcAuthSubject);
            Assert.Equal(ProviderName, mapping.ExternalProvider);
            Assert.Equal("ext-sub-1", mapping.ExternalSubject);
        }

        [Fact]
        public async Task Callback_IssuesB2CAuthorizationCodeBoundToStateAndRedirects()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback();

            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.StartsWith(RedirectUri + "?", redirect.Url);
            var query = HttpUtility.ParseQueryString(new Uri(redirect.Url).Query);
            // クライアントから受け取った state をそのまま返す（RFC 6749 Section 4.1.2）
            Assert.Equal("client-state", query["state"]);

            var code = await _context.AuthorizationCodes.SingleAsync(ac => ac.Code == query["code"]);
            var user = await _context.EcAuthUsers.SingleAsync();
            Assert.Equal(user.Subject, code.Subject);
            Assert.Equal(SubjectType.B2C, code.SubjectType);
            Assert.Equal(ClientDbId, code.ClientId);
            Assert.Equal(RedirectUri, code.RedirectUri);
            Assert.Equal("client-state", code.State);
            Assert.Equal(ValidChallenge, code.CodeChallenge);
            Assert.Equal("S256", code.CodeChallengeMethod);
        }

        [Fact]
        public async Task Callback_WithoutClientState_OmitsStateFromRedirect()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback(clientState: null);

            var redirect = Assert.IsType<RedirectResult>(result);
            var query = HttpUtility.ParseQueryString(new Uri(redirect.Url).Query);
            Assert.Null(query["state"]);
        }

        [Fact]
        public async Task Callback_SavesExternalIdpTokenForUser()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            await Callback();

            var user = await _context.EcAuthUsers.SingleAsync();
            _externalIdpTokenService.Verify(s => s.SaveTokenAsync(It.Is<IExternalIdpTokenService.SaveTokenRequest>(r =>
                r.EcAuthSubject == user.Subject &&
                r.ExternalProvider == ProviderName &&
                r.AccessToken == FakeIdpHandler.AccessToken)), Times.Once);
        }

        [Fact]
        public async Task Callback_SendsAuthorizationCodeAndProviderCredentialsToTokenEndpoint()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            await Callback();

            var form = HttpUtility.ParseQueryString(Assert.Single(_idp.TokenRequestBodies));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("idp-code", form["code"]);
            Assert.Equal("idp-client-id", form["client_id"]);
            Assert.Equal("idp-client-secret", form["client_secret"]);
            Assert.Equal("https://localhost:8081/v1/auth/callback", form["redirect_uri"]);
            Assert.Equal($"Bearer {FakeIdpHandler.AccessToken}", Assert.Single(_idp.UserinfoAuthorizationHeaders));
        }

        // ---- 重複作成の防止 ----

        [Fact]
        public async Task Callback_ExistingUser_ReusesUserWithoutDuplicating()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");
            await Callback();
            var first = await _context.EcAuthUsers.SingleAsync();

            var result = await Callback();

            var code = await _context.AuthorizationCodes.SingleAsync(ac => ac.Code == GetCode(result));
            Assert.Equal(first.Subject, code.Subject);
            Assert.Single(await _context.EcAuthUsers.ToListAsync());
            Assert.Single(await _context.ExternalIdpMappings.ToListAsync());
        }

        [Fact]
        public async Task Callback_ExistingUserWithChangedEmail_UpdatesEmailHash()
        {
            _idp.Userinfo("ext-sub-1", "old@example.com");
            await Callback();

            _idp.Userinfo("ext-sub-1", "new@example.com");
            await Callback();

            var user = await _context.EcAuthUsers.SingleAsync();
            Assert.Equal(EmailHashUtil.HashEmail("new@example.com"), user.EmailHash);
        }

        // ---- Organization 境界 ----

        [Fact]
        public async Task Callback_SameExternalSubjectInAnotherOrganization_CreatesSeparateUser()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");
            await Callback();

            await Callback(providerId: OtherProviderId, clientId: OtherClientDbId, organizationId: OtherOrganizationId);

            // InMemory はユニーク制約を検査しない。SQL Server では (external_provider, external_subject) の
            // ユニーク制約に当たる（UserServiceRelationalTests で固定）。ここではユーザー解決が
            // Organization で分かれていることだけを確認する。
            var users = await _context.EcAuthUsers.OrderBy(u => u.OrganizationId).ToListAsync();
            Assert.Equal(2, users.Count);
            Assert.Equal(new[] { OrganizationId, OtherOrganizationId }, users.Select(u => u.OrganizationId));
            Assert.NotEqual(users[0].Subject, users[1].Subject);
        }

        // ---- 異常系 ----

        [Fact]
        public async Task Callback_WithTamperedState_ReturnsServerErrorWithoutCreatingUser()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");
            var state = await SealState();
            var tampered = state[..^4] + (state.EndsWith("AAAA") ? "BBBB" : "AAAA");

            var result = await CreateController().Index("idp-code", tampered, "openid");

            // 現状は Iron.Unseal の例外を握って 500 を返す
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Empty(_idp.TokenRequestBodies);
            Assert.Empty(await _context.EcAuthUsers.ToListAsync());
        }

        [Fact]
        public async Task Callback_WithUnknownProvider_ReturnsBadRequest()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback(providerId: 999);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(_idp.TokenRequestBodies);
            Assert.Empty(await _context.EcAuthUsers.ToListAsync());
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
        [InlineData(HttpStatusCode.OK, "{\"token_type\":\"Bearer\"}")]
        [InlineData(HttpStatusCode.OK, "{\"access_token\":\"\"}")]
        public async Task Callback_WhenTokenExchangeFails_ReturnsBadRequestWithoutCreatingUser(HttpStatusCode status, string body)
        {
            _idp.TokenResponse(status, body);
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback();

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(_idp.UserinfoAuthorizationHeaders);
            Assert.Empty(await _context.EcAuthUsers.ToListAsync());
        }

        [Fact]
        public async Task Callback_WhenUserinfoFails_ReturnsBadRequestWithoutCreatingUser()
        {
            _idp.UserinfoResponse(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\"}");

            var result = await Callback();

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await _context.EcAuthUsers.ToListAsync());
            _externalIdpTokenService.Verify(s => s.SaveTokenAsync(It.IsAny<IExternalIdpTokenService.SaveTokenRequest>()), Times.Never);
        }

        [Fact]
        public async Task Callback_WhenUserinfoHasNoSubject_ReturnsServerError()
        {
            _idp.UserinfoResponse(HttpStatusCode.OK, "{\"email\":\"user@example.com\"}");

            var result = await Callback();

            // sub が無いと UserService が ArgumentException を投げ、500 になる
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Empty(await _context.EcAuthUsers.ToListAsync());
        }

        [Fact]
        public async Task Callback_WithUnknownClient_ReturnsBadRequestAfterProvisioningUser()
        {
            _idp.Userinfo("ext-sub-1", "user@example.com");

            var result = await Callback(clientId: 999);

            Assert.IsType<BadRequestObjectResult>(result);
            // 現状は Client の確認より先に JIT プロビジョニングが走るため、ユーザーは作成済みになる
            Assert.Single(await _context.EcAuthUsers.ToListAsync());
            Assert.Empty(await _context.AuthorizationCodes.ToListAsync());
        }

        [Fact]
        public async Task Callback_WithClientOfDeletedOrganization_IssuesCodeThatCannotBeLookedUp()
        {
            // 今の挙動の固定。Client 検索が IgnoreQueryFilters() なしで ExcludeDeletedOrganizations() を
            // 使っているため、Organization のクエリフィルターでナビゲーションが null になり素通りする
            // （ClientQueryExtensionsRelationalTests で SQLite でも同じことを確認）。発行された認可コードは
            // AuthorizationCode のクエリフィルターで見えなくなるため、/v1/token の交換では引けない。
            // EcAuthDocs#164 段 4（F-1）で直すときに、コードを発行しない形へ期待値を反転させる。
            _idp.Userinfo("ext-sub-1", "user@example.com");
            var org = await _context.Organizations.SingleAsync(o => o.Id == OrganizationId);
            // state は封緘済み（削除前に /v1/authorization を通過した）として扱う
            var state = await SealState();
            org.DeletedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            var result = await CreateController().Index("idp-code", state, "openid");

            var code = GetCode(result);
            Assert.Single(await _context.AuthorizationCodes.IgnoreQueryFilters().ToListAsync());
            var authorizationCodeService = new TestableAuthorizationCodeService(
                _context, new Mock<ILogger<AuthorizationCodeService>>().Object);
            Assert.Null(await authorizationCodeService.GetAuthorizationCodeAsync(code));
        }

        public void Dispose()
        {
            _context.Dispose();
            _idp.Dispose();
        }

        /// <summary>
        /// 外部 IdP のトークンエンドポイントと userinfo エンドポイントを模した handler。
        /// </summary>
        private sealed class FakeIdpHandler : HttpMessageHandler
        {
            public const string AccessToken = "idp-access-token";

            private HttpStatusCode _tokenStatus = HttpStatusCode.OK;
            private string _tokenBody = $"{{\"access_token\":\"{AccessToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}";
            private HttpStatusCode _userinfoStatus = HttpStatusCode.OK;
            private string _userinfoBody = "{}";

            public List<string> TokenRequestBodies { get; } = new();
            public List<string?> UserinfoAuthorizationHeaders { get; } = new();

            public void TokenResponse(HttpStatusCode status, string body)
            {
                _tokenStatus = status;
                _tokenBody = body;
            }

            public void UserinfoResponse(HttpStatusCode status, string body)
            {
                _userinfoStatus = status;
                _userinfoBody = body;
            }

            public void Userinfo(string sub, string email) =>
                UserinfoResponse(HttpStatusCode.OK, $"{{\"sub\":\"{sub}\",\"email\":\"{email}\"}}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
                if (request.Method == HttpMethod.Post && url == TokenEndpoint)
                {
                    TokenRequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                    return Json(_tokenStatus, _tokenBody);
                }
                if (request.Method == HttpMethod.Get && url == UserinfoEndpoint)
                {
                    UserinfoAuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
                    return Json(_userinfoStatus, _userinfoBody);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
