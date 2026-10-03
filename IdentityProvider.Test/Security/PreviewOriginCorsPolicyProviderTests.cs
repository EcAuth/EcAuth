using IdentityProvider.Controllers;
using IdentityProvider.Security;
using IdentityProvider.Services;
using IdentityProvider.Test.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IdentityProvider.Test.Security
{
    public class PreviewOriginCorsPolicyProviderTests
    {
        private const string PreviewOrigin = "https://abc123de.ecauth-website-stg.pages.dev";

        private static PreviewOriginCorsPolicyProvider CreateProvider()
        {
            var options = new CorsOptions();
            options.AddPolicy(SignupController.CorsPolicy, p => p
                .WithOrigins("https://ec-auth.io", "https://www.ec-auth.io")
                .AllowAnyHeader()
                .WithMethods("GET", "POST", "OPTIONS"));
            options.AddPolicy("Other", p => p.WithOrigins("https://ec-auth.io"));
            return new PreviewOriginCorsPolicyProvider(Options.Create(options));
        }

        private static HttpContext CreateContext(string tenantName, string origin)
        {
            var tenantService = new MockTenantService();
            tenantService.SetTenant(tenantName);
            var resolver = PreviewOriginResolverTests.Create(tenantName);

            var services = new ServiceCollection();
            services.AddSingleton<ITenantService>(tenantService);
            services.AddSingleton<IPreviewOriginResolver>(resolver);

            var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
            context.Request.Headers.Origin = origin;
            return context;
        }

        private static async Task<CorsResult> EvaluateAsync(string tenantName, string origin, string policyName)
        {
            var context = CreateContext(tenantName, origin);
            var policy = await CreateProvider().GetPolicyAsync(context, policyName);
            Assert.NotNull(policy);
            return new CorsService(Options.Create(new CorsOptions()), NullLoggerFactory.Instance)
                .EvaluatePolicy(context, policy!);
        }

        [Fact]
        public async Task PreviewOrigin_OnStgAccounts_IsAllowedWithVaryOrigin()
        {
            var result = await EvaluateAsync("stg-accounts", PreviewOrigin, SignupController.CorsPolicy);

            Assert.True(result.IsOriginAllowed);
            Assert.Equal(PreviewOrigin, result.AllowedOrigin);
            Assert.True(result.VaryByOrigin);
        }

        [Fact]
        public async Task PreviewOrigin_OnAccounts_IsRejected()
        {
            // accounts（live）にはパターンが無いので、固定オリジン以外は従来どおり拒否する。
            var result = await EvaluateAsync("accounts", PreviewOrigin, SignupController.CorsPolicy);

            Assert.False(result.IsOriginAllowed);
        }

        [Fact]
        public async Task OtherPolicy_IsNotExtended()
        {
            var result = await EvaluateAsync("stg-accounts", PreviewOrigin, "Other");

            Assert.False(result.IsOriginAllowed);
        }

        [Theory]
        [InlineData("stg-accounts")]
        [InlineData("accounts")]
        public async Task FixedOrigin_IsStillAllowed(string tenantName)
        {
            var result = await EvaluateAsync(tenantName, "https://ec-auth.io", SignupController.CorsPolicy);

            Assert.True(result.IsOriginAllowed);
            Assert.Equal("https://ec-auth.io", result.AllowedOrigin);
        }

        [Fact]
        public async Task OutsidePattern_OnStgAccounts_IsRejected()
        {
            var result = await EvaluateAsync(
                "stg-accounts", "https://abc.ecauth-website.pages.dev", SignupController.CorsPolicy);

            Assert.False(result.IsOriginAllowed);
        }
    }
}
