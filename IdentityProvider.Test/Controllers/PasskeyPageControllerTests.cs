using IdentityProvider.Controllers;
using IdentityProvider.Test.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class PasskeyPageControllerTests
    {
        private static PasskeyPageController CreateController(string tenantName, string? frontendBaseUrl = null)
        {
            var tenantService = new MockTenantService();
            tenantService.SetTenant(tenantName);
            var values = new Dictionary<string, string?>();
            if (frontendBaseUrl != null)
            {
                values["Frontend:BaseUrl"] = frontendBaseUrl;
            }
            return new PasskeyPageController(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                tenantService,
                PreviewOriginResolverTests.Create(tenantName));
        }

        private static object? FrontendBaseUrlOf(IActionResult result) =>
            Assert.IsType<ViewResult>(result).ViewData["FrontendBaseUrl"];

        [Fact]
        public void Register_PreviewFrontendOrigin_OnStgAccounts_UsesIt()
        {
            var result = CreateController("stg-accounts").Register("https://abc.ecauth-website-stg.pages.dev");

            Assert.Equal("https://abc.ecauth-website-stg.pages.dev", FrontendBaseUrlOf(result));
        }

        [Fact]
        public void Authenticate_PreviewFrontendOrigin_OnStgAccounts_UsesIt()
        {
            var result = CreateController("stg-accounts").Authenticate("https://abc.ecauth-website-stg.pages.dev");

            Assert.Equal("https://abc.ecauth-website-stg.pages.dev", FrontendBaseUrlOf(result));
        }

        [Theory]
        [InlineData("https://evil.example")]
        [InlineData("https://abc.ecauth-website-stg.pages.dev/mypage/")]
        [InlineData("javascript:alert(1)")]
        [InlineData(null)]
        public void Register_UnallowedFrontendOrigin_FallsBackToConfigured(string? frontendOrigin)
        {
            var result = CreateController("stg-accounts", "https://stg-preview.ec-auth.io/").Register(frontendOrigin);

            Assert.Equal("https://stg-preview.ec-auth.io", FrontendBaseUrlOf(result));
        }

        [Fact]
        public void Register_PreviewFrontendOrigin_OnAccounts_IsIgnored()
        {
            var result = CreateController("accounts").Register("https://abc.ecauth-website-stg.pages.dev");

            Assert.Equal("https://ec-auth.io", FrontendBaseUrlOf(result));
        }
    }
}
