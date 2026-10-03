using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IdentityProvider.Test.Services
{
    public class PreviewOriginResolverTests
    {
        private const string StgTenant = "stg-accounts";
        private const string Pattern = "https://*.ecauth-website-stg.pages.dev";

        internal static PreviewOriginResolver Create(
            string tenantName = StgTenant,
            string? requestOrigin = null,
            params string[] patterns)
        {
            var values = new Dictionary<string, string?>();
            var effective = patterns.Length == 0 ? new[] { Pattern } : patterns;
            for (var i = 0; i < effective.Length; i++)
            {
                values[$"PreviewOrigins:stg_accounts:{i}"] = effective[i];
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            var tenantService = new MockTenantService();
            tenantService.SetTenant(tenantName);

            var httpContext = new DefaultHttpContext();
            if (requestOrigin != null)
            {
                httpContext.Request.Headers.Origin = requestOrigin;
            }

            return new PreviewOriginResolver(
                configuration,
                tenantService,
                new HttpContextAccessor { HttpContext = httpContext },
                NullLogger<PreviewOriginResolver>.Instance);
        }

        [Theory]
        [InlineData("https://abc123de.ecauth-website-stg.pages.dev")]
        [InlineData("https://issue-159-stg-preview.ecauth-website-stg.pages.dev")]
        [InlineData("https://ABC123DE.ecauth-website-stg.pages.dev")]
        public void IsAllowed_SingleLabelUnderPattern_ReturnsTrue(string origin)
        {
            Assert.True(Create().IsAllowed(StgTenant, origin));
        }

        [Theory]
        // パターンの外・別プロジェクト
        [InlineData("https://ecauth-website-stg.pages.dev")]
        [InlineData("https://abc.ecauth-website.pages.dev")]
        [InlineData("https://abc.evil-ecauth-website-stg.pages.dev")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev.evil.example")]
        // ワイルドカードは 1 ラベルだけ
        [InlineData("https://a.b.ecauth-website-stg.pages.dev")]
        [InlineData("https://-abc.ecauth-website-stg.pages.dev")]
        // オリジンの正規形以外（scheme / port / path / 末尾スラッシュ / userinfo）
        [InlineData("http://abc.ecauth-website-stg.pages.dev")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev:8443")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev/")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev/mypage/")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev?x=1")]
        [InlineData("https://user@abc.ecauth-website-stg.pages.dev")]
        [InlineData("null")]
        [InlineData("")]
        [InlineData(null)]
        public void IsAllowed_OutsidePatternOrNonCanonical_ReturnsFalse(string? origin)
        {
            Assert.False(Create().IsAllowed(StgTenant, origin));
        }

        [Fact]
        public void IsAllowed_TenantWithoutPatterns_ReturnsFalse()
        {
            // accounts（本番 live）にはパターンを設定しないので、同じオリジンでも許可しない。
            Assert.False(Create().IsAllowed("accounts", "https://abc.ecauth-website-stg.pages.dev"));
        }

        [Theory]
        [InlineData("https://*.pages.dev")]   // 他人の Pages プロジェクトまで含む
        [InlineData("https://*.ec-auth.io")]  // 2 ラベル（広すぎる）
        [InlineData("https://abc.ecauth-website-stg.pages.dev")] // ワイルドカードでない
        [InlineData("http://*.ecauth-website-stg.pages.dev")]
        [InlineData("https://*.*.pages.dev")]
        public void IsAllowed_InvalidPattern_IsIgnored(string pattern)
        {
            var resolver = Create(patterns: pattern);
            Assert.False(resolver.IsAllowed(StgTenant, "https://abc.ecauth-website-stg.pages.dev"));
            Assert.False(resolver.IsAllowed(StgTenant, "https://pages.dev"));
        }

        [Fact]
        public void IsAllowed_PatternWithTrailingSlashAndCase_IsNormalized()
        {
            var resolver = Create(patterns: "  HTTPS://*.EcAuth-Website-Stg.Pages.Dev/ ");
            Assert.True(resolver.IsAllowed(StgTenant, "https://abc.ecauth-website-stg.pages.dev"));
        }

        [Fact]
        public void ResolveRequestOrigin_AllowedOrigin_ReturnsIt()
        {
            var resolver = Create(requestOrigin: "https://abc.ecauth-website-stg.pages.dev");
            Assert.Equal("https://abc.ecauth-website-stg.pages.dev", resolver.ResolveRequestOrigin());
        }

        [Theory]
        [InlineData("https://ec-auth.io")]
        [InlineData("https://abc.ecauth-website.pages.dev")]
        [InlineData(null)]
        public void ResolveRequestOrigin_NotAllowed_ReturnsNull(string? origin)
        {
            Assert.Null(Create(requestOrigin: origin).ResolveRequestOrigin());
        }

        [Fact]
        public void ResolveRequestOrigin_OtherTenant_ReturnsNull()
        {
            var resolver = Create(tenantName: "accounts", requestOrigin: "https://abc.ecauth-website-stg.pages.dev");
            Assert.Null(resolver.ResolveRequestOrigin());
        }
    }
}
