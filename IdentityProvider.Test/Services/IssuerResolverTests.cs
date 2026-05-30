using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IdentityProvider.Test.Services
{
    public class IssuerResolverTests
    {
        [Fact]
        public void GetIssuer_WithHttpContext_ReturnsSchemeAndHost()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("acme.ec-cube.io");
            var accessor = new HttpContextAccessor { HttpContext = httpContext };
            var resolver = new IssuerResolver(accessor);

            // Act
            var issuer = resolver.GetIssuer();

            // Assert
            Assert.Equal("https://acme.ec-cube.io", issuer);
        }

        [Theory]
        [InlineData("https", "example.com", "https://example.com")]
        [InlineData("http", "localhost:8080", "http://localhost:8080")]
        [InlineData("https", "tenant.example.co.jp:9443", "https://tenant.example.co.jp:9443")]
        public void GetIssuer_VariousSchemeAndHost_ReturnsExpected(string scheme, string host, string expected)
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = scheme;
            httpContext.Request.Host = new HostString(host);
            var accessor = new HttpContextAccessor { HttpContext = httpContext };
            var resolver = new IssuerResolver(accessor);

            // Act
            var issuer = resolver.GetIssuer();

            // Assert
            Assert.Equal(expected, issuer);
        }

        [Fact]
        public void GetIssuer_NullHttpContext_ThrowsInvalidOperationException()
        {
            // Arrange - HttpContext を持たない accessor（バックグラウンド処理等を想定）
            var accessor = new HttpContextAccessor { HttpContext = null };
            var resolver = new IssuerResolver(accessor);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => resolver.GetIssuer());
        }
    }
}
