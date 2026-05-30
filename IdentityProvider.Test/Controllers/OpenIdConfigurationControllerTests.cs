using IdentityProvider.Controllers;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class OpenIdConfigurationControllerTests
    {
        private readonly Mock<IIssuerResolver> _mockIssuerResolver;
        private readonly OpenIdConfigurationController _controller;

        private const string TestIssuer = "https://tenant.ec-cube.io";

        public OpenIdConfigurationControllerTests()
        {
            _mockIssuerResolver = new Mock<IIssuerResolver>();
            _mockIssuerResolver.Setup(x => x.GetIssuer()).Returns(TestIssuer);
            _controller = new OpenIdConfigurationController(_mockIssuerResolver.Object);
        }

        private static Dictionary<string, object> GetMetadata(IActionResult result)
        {
            var jsonResult = Assert.IsType<JsonResult>(result);
            var metadata = Assert.IsType<Dictionary<string, object>>(jsonResult.Value);
            return metadata;
        }

        [Fact]
        public void Get_ReturnsRequiredDiscoveryFields()
        {
            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - 確定フィールドが全て含まれること
            Assert.Equal(TestIssuer, metadata["issuer"]);
            Assert.Equal($"{TestIssuer}/v1/token", metadata["token_endpoint"]);
            Assert.Equal($"{TestIssuer}/v1/userinfo", metadata["userinfo_endpoint"]);
            Assert.Equal($"{TestIssuer}/.well-known/jwks.json", metadata["jwks_uri"]);

            Assert.Equal(new[] { "authorization_code" }, Assert.IsType<string[]>(metadata["grant_types_supported"]));
            Assert.Equal(new[] { "client_secret_post", "none" }, Assert.IsType<string[]>(metadata["token_endpoint_auth_methods_supported"]));
            Assert.Equal(new[] { "RS256" }, Assert.IsType<string[]>(metadata["id_token_signing_alg_values_supported"]));
            Assert.Equal(new[] { "public" }, Assert.IsType<string[]>(metadata["subject_types_supported"]));
            Assert.Equal(new[] { "openid", "email", "profile" }, Assert.IsType<string[]>(metadata["scopes_supported"]));
            Assert.Equal(new[] { "sub" }, Assert.IsType<string[]>(metadata["claims_supported"]));
        }

        [Fact]
        public void Get_IssuerReflectsRequestHost()
        {
            // Arrange - 別ホストを返す resolver に差し替え
            var otherIssuer = "https://another-tenant.example.com";
            _mockIssuerResolver.Setup(x => x.GetIssuer()).Returns(otherIssuer);

            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - issuer がリクエスト Host を反映すること
            Assert.Equal(otherIssuer, metadata["issuer"]);
            // 各 endpoint URL が issuer を基底に組み立てられること
            Assert.Equal($"{otherIssuer}/v1/token", metadata["token_endpoint"]);
            Assert.Equal($"{otherIssuer}/v1/userinfo", metadata["userinfo_endpoint"]);
            Assert.Equal($"{otherIssuer}/.well-known/jwks.json", metadata["jwks_uri"]);
        }

        [Fact]
        public void Get_AllEndpointUrlsAreRootedAtIssuer()
        {
            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - URL を含む全フィールドが issuer を前置していること
            foreach (var key in new[] { "token_endpoint", "userinfo_endpoint", "jwks_uri" })
            {
                var url = Assert.IsType<string>(metadata[key]);
                Assert.StartsWith(TestIssuer + "/", url);
            }
        }

        [Fact]
        public void Get_DoesNotExposeAuthorizationEndpoint()
        {
            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - /v1/authorization は非標準のフェデレーション proxy のため
            // authorization_endpoint は意図的に省略される（設計判断）。
            Assert.False(metadata.ContainsKey("authorization_endpoint"),
                "authorization_endpoint は意図的に省略されるべき（非標準フェデレーション proxy のため）");
        }

        [Fact]
        public void Get_DoesNotExposeResponseTypesSupported()
        {
            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - authorization_endpoint 非公開に伴い response_types_supported も省略される。
            Assert.False(metadata.ContainsKey("response_types_supported"),
                "response_types_supported は意図的に省略されるべき（authorization_endpoint 非公開に伴う）");
        }

        [Fact]
        public void Get_DoesNotExposeCodeChallengeMethodsSupported()
        {
            // Act
            var metadata = GetMetadata(_controller.Get());

            // Assert - PKCE 未実装のため code_challenge_methods_supported も省略される。
            Assert.False(metadata.ContainsKey("code_challenge_methods_supported"),
                "code_challenge_methods_supported は意図的に省略されるべき（PKCE 未実装のため）");
        }
    }
}
