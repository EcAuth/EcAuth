using System.IdentityModel.Tokens.Jwt;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using IdpUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    /// <summary>
    /// 回帰防止: OpenID Connect Discovery が返す issuer と、TokenService が発行する
    /// ID Token の iss クレームが「完全一致」することを保証する。
    /// 両者は同一の IIssuerResolver を単一ソースとして参照するため、同じ HttpContext
    /// （= 同じ scheme/host）を与えれば必ず一致しなければならない。
    /// この一致が崩れると RP 側の iss 検証が失敗するため、最重要の不変条件として固定する。
    /// </summary>
    public class IssuerConsistencyTests
    {
        [Theory]
        [InlineData("https", "tenant.ec-cube.io")]
        [InlineData("https", "another.example.com:9443")]
        [InlineData("http", "localhost:8080")]
        public async Task DiscoveryIssuer_AndIdTokenIssClaim_AreIdentical(string scheme, string host)
        {
            // Arrange - Discovery と TokenService に「同一の」IIssuerResolver を注入する
            var issuerResolver = TestDbContextHelper.CreateIssuerResolver(scheme: scheme, host: host);

            using var context = TestDbContextHelper.CreateInMemoryContext();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var tokenService = new TokenService(context, loggerFactory.CreateLogger<TokenService>(), issuerResolver);

            var (client, user) = await SetupTestDataAsync(context);

            // Act - Discovery の issuer を取得
            var discoveryController = new OpenIdConfigurationController(issuerResolver);
            var jsonResult = Assert.IsType<JsonResult>(discoveryController.Get());
            var metadata = Assert.IsType<Dictionary<string, object>>(jsonResult.Value);
            var discoveryIssuer = Assert.IsType<string>(metadata["issuer"]);

            // ID Token を生成し iss クレームを取得
            var idToken = await tokenService.GenerateIdTokenAsync(new ITokenService.TokenRequest
            {
                User = user,
                Client = client,
                RequestedScopes = new[] { "openid" }
            });
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
            var issClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value;

            // Assert - 完全一致
            Assert.Equal($"{scheme}://{host}", discoveryIssuer);
            Assert.Equal(discoveryIssuer, issClaim);
        }

        private static async Task<(Client client, EcAuthUser user)> SetupTestDataAsync(EcAuthDbContext context)
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

            TestDbContextHelper.GenerateAndAddRsaKeyPair(context, organization, 1);

            await context.SaveChangesAsync();
            return (client, user);
        }
    }
}
