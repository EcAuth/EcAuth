using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    /// <summary>
    /// /v1/authorization（B2C フェデレーション）の PKCE (RFC 7636) パラメータ検証と、
    /// 封緘 State への往復保持を検証する。
    ///
    /// code_challenge を受け取る場所（本コントローラ）と認可コードを発行する場所
    /// （AuthorizationCallbackController）が外部 IdP の往復を挟んで分かれるため、
    /// 「State に載せて往復しても challenge が保たれること」がこのフローの要になる。
    /// </summary>
    public class AuthorizationControllerTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly AuthorizationController _controller;

        private const string ClientId = "test-client";
        private const string ProviderName = "federate-oauth2";
        private const string RedirectUri = "https://client.example.com/callback";

        // Iron はパスワード長 32 文字以上を要求する
        private const string StatePassword = "test-state-password-must-be-32-chars-or-longer";

        // S256 の code_challenge は base64url(SHA256(verifier)) = 43 文字
        private const string ValidChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        public AuthorizationControllerTests()
        {
            Environment.SetEnvironmentVariable("STATE_PASSWORD", StatePassword);

            _context = TestDbContextHelper.CreateInMemoryContext();
            SeedClientAndProvider();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEFAULT_ORGANIZATION_REDIRECT_URI"] = "https://localhost:8081/v1/auth/callback"
                })
                .Build();

            _controller = new AuthorizationController(
                _context,
                new MockTenantService(),
                configuration,
                new Mock<ILogger<AuthorizationController>>().Object);
        }

        private void SeedClientAndProvider()
        {
            _context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "test-org",
                Name = "Test Org",
                TenantName = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            _context.Clients.Add(new Client
            {
                Id = 1,
                ClientId = ClientId,
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1
            });
            _context.OpenIdProviders.Add(new OpenIdProvider
            {
                Id = 1,
                Name = ProviderName,
                IdpClientId = "idp-client-id",
                IdpClientSecret = "idp-client-secret",
                AuthorizationEndpoint = "https://idp.example.com/authorize",
                ClientId = 1
            });
            _context.SaveChanges();
        }

        private Task<IActionResult> Federate(string? codeChallenge, string? codeChallengeMethod) =>
            _controller.Federate(ClientId, ProviderName, RedirectUri, "client-state", codeChallenge, codeChallengeMethod);

        /// <summary>リダイレクト先の state パラメータを開封して State を取り出す。</summary>
        private static async Task<State> UnsealStateFrom(IActionResult result)
        {
            var redirect = Assert.IsType<RedirectResult>(result);
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(redirect.Url).Query);
            var sealedState = query["state"];
            Assert.False(string.IsNullOrEmpty(sealedState));
            return await Iron.Unseal<State>(sealedState!, StatePassword, new Iron.Options());
        }

        private static string GetError(IActionResult result)
        {
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            return (string)badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value)!;
        }

        [Fact]
        public async Task Federate_WithoutCodeChallenge_DoesNotBindPkce()
        {
            // 後方互換: PKCE 未指定の既存クライアントは従来どおり動作する
            var result = await Federate(null, null);

            var state = await UnsealStateFrom(result);
            Assert.Null(state.CodeChallenge);
            Assert.Null(state.CodeChallengeMethod);
            Assert.Equal("client-state", state.ClientState);
        }

        [Fact]
        public async Task Federate_WithCodeChallengeAndNoMethod_DefaultsToS256()
        {
            var result = await Federate(ValidChallenge, null);

            var state = await UnsealStateFrom(result);
            Assert.Equal(ValidChallenge, state.CodeChallenge);
            Assert.Equal("S256", state.CodeChallengeMethod);
        }

        [Fact]
        public async Task Federate_WithExplicitS256_PreservesChallengeThroughSealedState()
        {
            var result = await Federate(ValidChallenge, "S256");

            var state = await UnsealStateFrom(result);
            Assert.Equal(ValidChallenge, state.CodeChallenge);
            Assert.Equal("S256", state.CodeChallengeMethod);
            // State の他の項目も併せて往復していること
            Assert.Equal(RedirectUri, state.RedirectUri);
            Assert.Equal(1, state.ClientId);
            Assert.Equal(1, state.OpenIdProviderId);
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("s256")] // 大文字小文字は区別する（RFC 7636 は "S256"）
        [InlineData("S512")]
        public async Task Federate_WithUnsupportedMethod_ReturnsInvalidRequest(string method)
        {
            var result = await Federate(ValidChallenge, method);

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithTooShortChallenge_ReturnsInvalidRequest()
        {
            // RFC 7636 Section 4.2: code_challenge = 43*128unreserved
            var result = await Federate(new string('a', 42), "S256");

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithTooLongChallenge_ReturnsInvalidRequest()
        {
            // AuthorizationCode.CodeChallenge は MaxLength(128)。ここで弾かないと
            // 認可コード発行時に桁溢れで 500 になる
            var result = await Federate(new string('a', 129), "S256");

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithInvalidCharsInChallenge_ReturnsInvalidRequest()
        {
            // unreserved 以外（"+" "/" "=" は base64url では現れない）
            var result = await Federate("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+c=", "S256");

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithMethodButNoChallenge_ReturnsInvalidRequest()
        {
            // 黙って無視するとクライアントは PKCE が効いていると誤認する
            var result = await Federate(null, "S256");

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithUnknownClientId_ReturnsInvalidRequest()
        {
            var result = await _controller.Federate(
                "no-such-client", ProviderName, RedirectUri, null, null, null);

            Assert.Equal("invalid_request", GetError(result));
        }

        [Fact]
        public async Task Federate_WithUnknownProviderName_ReturnsInvalidRequest()
        {
            var result = await _controller.Federate(
                ClientId, "no-such-provider", RedirectUri, null, null, null);

            Assert.Equal("invalid_request", GetError(result));
        }

        public void Dispose()
        {
            _context.Dispose();
            Environment.SetEnvironmentVariable("STATE_PASSWORD", null);
        }
    }
}
