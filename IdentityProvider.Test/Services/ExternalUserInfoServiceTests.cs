using System.Net;
using System.Text;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="ExternalUserInfoService"/> が、provider 名とユーザーのプール（Client / Organization）から
    /// 正しい OpenIdProvider を解決することを検証する（EcAuthDocs#122）。
    ///
    /// 旧実装は ExternalIdpMapping を必須にし、OpenIdProvider を名前だけで引いていた。識別モデルの
    /// 導入後は、旧 mapping を持たない（旧制約に当たって書けなかった）ユーザーがいるうえ、
    /// 名前は Client 間で衝突しうる。
    /// </summary>
    public sealed class ExternalUserInfoServiceTests : IDisposable
    {
        private readonly EcAuthDbContext _context = TestDbContextHelper.CreateInMemoryContext();
        private readonly Mock<IExternalIdpTokenService> _tokens = new();
        private readonly RecordingHandler _http = new();
        private readonly ExternalUserInfoService _service;

        public ExternalUserInfoServiceTests()
        {
            _context.Organizations.Add(new Organization { Id = 10, Code = "org-a", Name = "A", TenantName = "test-tenant" });
            _context.Organizations.Add(new Organization { Id = 20, Code = "org-b", Name = "B", TenantName = "test-tenant" });
            _context.Clients.Add(new Client { Id = 1, ClientId = "client-a", ClientSecret = "s", AppName = "A", OrganizationId = 10 });
            _context.Clients.Add(new Client { Id = 2, ClientId = "client-a2", ClientSecret = "s", AppName = "A2", OrganizationId = 10 });
            _context.Clients.Add(new Client { Id = 3, ClientId = "client-b", ClientSecret = "s", AppName = "B", OrganizationId = 20 });
            // 同じ provider 名が 3 つの Client にある
            _context.OpenIdProviders.Add(Provider(101, 1, "https://idp-a.example.com/userinfo"));
            _context.OpenIdProviders.Add(Provider(201, 2, "https://idp-a2.example.com/userinfo"));
            _context.OpenIdProviders.Add(Provider(301, 3, "https://idp-b.example.com/userinfo"));
            _context.SaveChanges();

            _tokens.Setup(t => t.GetTokenAsync(It.IsAny<string>(), "google"))
                .ReturnsAsync(new ExternalIdpToken { AccessToken = "idp-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(AuthorizationCallbackController.ExternalIdpHttpClientName))
                .Returns(() => new HttpClient(_http, disposeHandler: false));
            _service = new ExternalUserInfoService(_context, _tokens.Object, factory.Object,
                new Mock<ILogger<ExternalUserInfoService>>().Object);
        }

        private static OpenIdProvider Provider(int id, int clientId, string userinfo) => new()
        {
            Id = id,
            Name = "google",
            IdpClientId = "x",
            IdpClientSecret = "y",
            UserinfoEndpoint = userinfo,
            ClientId = clientId
        };

        private void SeedUser(string subject, int orgId, string? clientId, int? identityProviderId = null, bool legacyMapping = false)
        {
            var user = new EcAuthUser { Subject = subject, EmailHash = "", OrganizationId = orgId, ClientId = clientId };
            _context.EcAuthUsers.Add(user);
            if (identityProviderId is int pid)
            {
                _context.EcAuthUserIdentities.Add(new EcAuthUserIdentity
                {
                    EcAuthSubject = subject,
                    IssuerKey = B2CIssuerKey.ForIdp(pid),
                    ExternalId = ExternalIdHasher.HashCaseSensitive("sub"),
                    ClientId = clientId,
                    EcAuthUser = user
                });
            }
            if (legacyMapping)
            {
                _context.ExternalIdpMappings.Add(new ExternalIdpMapping
                {
                    EcAuthSubject = subject,
                    ExternalProvider = "google",
                    ExternalSubject = "sub",
                    EcAuthUser = user
                });
            }
            _context.SaveChanges();
        }

        private Task<IExternalUserInfoService.ExternalUserInfo?> Get(string subject) =>
            _service.GetExternalUserInfoAsync(new IExternalUserInfoService.GetExternalUserInfoRequest
            {
                EcAuthSubject = subject,
                ExternalProvider = "google"
            });

        [Fact]
        public async Task IdentityOnlyUser_FetchesFromProviderOfOwnClient()
        {
            // 旧 mapping を持たない（旧制約に当たって書けなかった）ユーザー
            SeedUser("u-a2", 10, "client-a2", identityProviderId: 201);

            var result = await Get("u-a2");

            Assert.NotNull(result);
            Assert.Equal("https://idp-a2.example.com/userinfo", Assert.Single(_http.Requests));
        }

        [Fact]
        public async Task UserInAnotherOrganization_UsesThatOrganizationsProvider()
        {
            SeedUser("u-b", 20, "client-b", identityProviderId: 301);

            await Get("u-b");

            Assert.Equal("https://idp-b.example.com/userinfo", Assert.Single(_http.Requests));
        }

        [Fact]
        public async Task LegacyUserWithoutClient_UsesSingleProviderInOrganization()
        {
            // backfill で Client を決められず、まだ再ログインしていない旧ユーザー。Organization B の google は 1 つ
            SeedUser("legacy-b", 20, clientId: null, legacyMapping: true);

            await Get("legacy-b");

            Assert.Equal("https://idp-b.example.com/userinfo", Assert.Single(_http.Requests));
        }

        [Fact]
        public async Task LegacyUserWithAmbiguousProvider_ReturnsNull()
        {
            // Organization A には google が 2 つ（client-a / client-a2）あり、どちらか決められない
            SeedUser("legacy-a", 10, clientId: null, legacyMapping: true);

            Assert.Null(await Get("legacy-a"));
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task UserNotLinkedToProvider_ReturnsNull()
        {
            SeedUser("u-a", 10, "client-a");

            Assert.Null(await Get("u-a"));
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task UnknownSubject_ReturnsNull()
        {
            Assert.Null(await Get("nobody"));
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task SendsStoredAccessTokenAsBearer()
        {
            SeedUser("u-a", 10, "client-a", identityProviderId: 101);

            var result = await Get("u-a");

            Assert.NotNull(result);
            Assert.Equal("Bearer idp-token", Assert.Single(_http.Authorizations));
        }

        public void Dispose()
        {
            _context.Dispose();
            _http.Dispose();
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<string> Requests { get; } = new();
            public List<string?> Authorizations { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!.ToString());
                Authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"sub\":\"sub\"}", Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
