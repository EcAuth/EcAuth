using IdentityProvider.Security;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IdentityProvider.Test.Security
{
    /// <summary>
    /// AllowedHosts の補完（EcAuthDocs#102）。
    /// appsettings の許可リストに App Service の既定ホスト（WEBSITE_HOSTNAME）を足す挙動と、
    /// appsettings に書いたパターン（<c>*.ec-auth.io</c>）が HostFiltering でどう解釈されるかを固定する。
    /// </summary>
    public class HostFilteringSetupTests
    {
        private static IConfiguration Config(string? websiteHostname)
        {
            var values = new Dictionary<string, string?>();
            if (websiteHostname != null)
            {
                values[HostFilteringSetup.WebsiteHostnameKey] = websiteHostname;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        /// <summary>
        /// 既定の PostConfigure は AllowedHosts を固定長配列（string[]）で設定する。
        /// Add すると NotSupportedException になるため、その形で渡して動くことを確認する。
        /// </summary>
        private static HostFilteringOptions OptionsWith(params string[] hosts) =>
            new HostFilteringOptions { AllowedHosts = hosts };

        [Fact]
        public void AddWebsiteHostname_WhenUnset_LeavesAllowedHostsUnchanged()
        {
            var options = OptionsWith("*.ec-auth.io");

            HostFilteringSetup.AddWebsiteHostname(options, Config(null));

            Assert.Equal(new[] { "*.ec-auth.io" }, options.AllowedHosts);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AddWebsiteHostname_WhenBlank_LeavesAllowedHostsUnchanged(string blank)
        {
            var options = OptionsWith("*.ec-auth.io");

            HostFilteringSetup.AddWebsiteHostname(options, Config(blank));

            Assert.Equal(new[] { "*.ec-auth.io" }, options.AllowedHosts);
        }

        [Fact]
        public void AddWebsiteHostname_WhenSet_AppendsToFixedSizeArray()
        {
            var options = OptionsWith("*.ec-auth.io");

            HostFilteringSetup.AddWebsiteHostname(options, Config("ecauth-prod-example.azurewebsites.net"));

            Assert.Equal(new[] { "*.ec-auth.io", "ecauth-prod-example.azurewebsites.net" }, options.AllowedHosts);
        }

        [Fact]
        public void AddWebsiteHostname_WhenAlreadyAllowed_DoesNotDuplicate()
        {
            var options = OptionsWith("*.ec-auth.io", "ECAUTH-PROD-EXAMPLE.azurewebsites.net");

            HostFilteringSetup.AddWebsiteHostname(options, Config("ecauth-prod-example.azurewebsites.net"));

            Assert.Equal(2, options.AllowedHosts.Count);
        }

        [Fact]
        public void AddWebsiteHostname_WhenWildcard_DoesNothing()
        {
            var options = OptionsWith("*");

            HostFilteringSetup.AddWebsiteHostname(options, Config("ecauth-prod-example.azurewebsites.net"));

            Assert.Equal(new[] { "*" }, options.AllowedHosts);
        }

        // ---- appsettings に書いたパターンの解釈（HostFilteringMiddleware が使う HostString.MatchesAny）----
        //
        // 本番の AllowedHosts は "*.ec-auth.io"。テナントのサブドメインは通り、apex（Cloudflare Pages）や
        // 攻撃者ドメインは通らず、ポートは無視されることをここで固定する。

        [Theory]
        [InlineData("accounts.ec-auth.io", true)]
        [InlineData("production.ec-auth.io", true)]
        [InlineData("e2e-abc-shop4-test.ec-auth.io", true)]
        [InlineData("accounts.ec-auth.io:8081", true)]
        [InlineData("ACCOUNTS.EC-AUTH.IO", true)]
        [InlineData("ec-auth.io", false)]
        [InlineData("evil.attacker.example", false)]
        [InlineData("ec-auth.io.evil.example", false)]
        [InlineData("xec-auth.io", false)]
        [InlineData("ecauth-prod-example.azurewebsites.net", false)]
        public void ProductionPattern_MatchesOnlySubdomainsOfEcAuthIo(string host, bool expected)
        {
            var patterns = new List<StringSegment> { "*.ec-auth.io" };

            Assert.Equal(expected, HostString.MatchesAny(host, patterns));
        }

        [Theory]
        [InlineData("ecauth-prod-example.azurewebsites.net", true)]
        [InlineData("other.azurewebsites.net", false)]
        public void WebsiteHostname_IsExactMatchOnly(string host, bool expected)
        {
            var patterns = new List<StringSegment> { "*.ec-auth.io", "ecauth-prod-example.azurewebsites.net" };

            Assert.Equal(expected, HostString.MatchesAny(host, patterns));
        }
    }
}
