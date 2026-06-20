using System.Net;
using IdentityProvider.Services;
using Xunit;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="CloudflareOptions"/> の既定 CIDR が ForwardedHeaders の KnownNetworks に
    /// 登録できる形式であることを検証する（タイポによる信頼境界の取りこぼしを防ぐ）。
    /// </summary>
    public class CloudflareOptionsTests
    {
        [Fact]
        public void DefaultTrustedIpRanges_AreAllParsableCidrs()
        {
            var options = new CloudflareOptions();

            Assert.NotEmpty(options.TrustedIpRanges);
            foreach (var cidr in options.TrustedIpRanges)
            {
                Assert.True(
                    IPNetwork.TryParse(cidr, out _),
                    $"Cloudflare の CIDR がパースできません: {cidr}");
            }
        }

        [Fact]
        public void DefaultTrustedIpRanges_ContainBothIpv4AndIpv6()
        {
            var options = new CloudflareOptions();

            var hasIpv4 = options.TrustedIpRanges.Exists(c =>
                IPNetwork.TryParse(c, out var n) && n.BaseAddress.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetwork);
            var hasIpv6 = options.TrustedIpRanges.Exists(c =>
                IPNetwork.TryParse(c, out var n) && n.BaseAddress.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetworkV6);

            Assert.True(hasIpv4, "IPv4 レンジが含まれていません。");
            Assert.True(hasIpv6, "IPv6 レンジが含まれていません。");
        }
    }
}
