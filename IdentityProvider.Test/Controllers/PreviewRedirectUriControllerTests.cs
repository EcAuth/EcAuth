using System.Text;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Test.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class PreviewRedirectUriControllerTests
    {
        private const string StgTenant = "stg-accounts";
        private const string OpsClientId = "preview-ops";
        private const string OpsClientSecret = "preview-ops-secret";
        private const string FixedRedirectUri = "https://stg-preview.ec-auth.io/auth/callback";
        private const string PreviewRedirectUri = "https://issue-159.ecauth-website-stg.pages.dev/auth/callback";

        private static EcAuthDbContext CreateContext(string tenantName = StgTenant)
        {
            var tenantService = new MockTenantService();
            tenantService.SetTenant(tenantName);
            var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);

            context.Organizations.Add(new Organization { Id = 1, Code = StgTenant, Name = "EcAuth Accounts (Staging)", TenantName = StgTenant });
            var client = new Client
            {
                ClientId = "ecauth-admin-console",
                ClientSecret = "secret",
                AppName = "EcAuth Accounts (Staging)",
                OrganizationId = 1,
                SubjectType = SubjectType.Account
            };
            client.RedirectUris!.Add(new RedirectUri { Uri = FixedRedirectUri });
            context.Clients.Add(client);
            context.SaveChanges();
            return context;
        }

        private static PreviewRedirectUriController CreateController(
            EcAuthDbContext context,
            string tenantName = StgTenant,
            bool configured = true,
            string? authorization = null)
        {
            var tenantService = new MockTenantService();
            tenantService.SetTenant(tenantName);

            var values = new Dictionary<string, string?>();
            if (configured)
            {
                values["PreviewRedirectApi:ClientId:stg_accounts"] = OpsClientId;
                values["PreviewRedirectApi:ClientSecret:stg_accounts"] = OpsClientSecret;
            }

            var controller = new PreviewRedirectUriController(
                context,
                tenantService,
                PreviewOriginResolverTests.Create(tenantName),
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                NullLogger<PreviewRedirectUriController>.Instance);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization =
                authorization ?? Basic(OpsClientId, OpsClientSecret);
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private static string Basic(string id, string secret) =>
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}"));

        private static List<string> Uris(EcAuthDbContext context) =>
            context.RedirectUris.AsNoTracking().Select(r => r.Uri).OrderBy(u => u).ToList();

        private static int StatusOf(IActionResult result) => result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => throw new InvalidOperationException(result.GetType().Name)
        };

        [Fact]
        public async Task Register_PreviewUri_Adds()
        {
            using var context = CreateContext();

            var result = await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(201, StatusOf(result));
            Assert.Equal(new[] { FixedRedirectUri, PreviewRedirectUri }.OrderBy(u => u), Uris(context));
        }

        [Fact]
        public async Task Register_OtherOrganizationWithSameTenantName_IsNotUsed()
        {
            // tenant_name は一意制約が無い。受付 Organization（code == tenant_name）以外の Account Client に付けない。
            var tenantService = new MockTenantService();
            tenantService.SetTenant(StgTenant);
            using var context = TestDbContextHelper.CreateInMemoryContext(tenantService: tenantService);
            context.Organizations.Add(new Organization { Id = 2, Code = "other-org", Name = "Other", TenantName = StgTenant });
            context.Clients.Add(new Client
            {
                ClientId = "other-account-client",
                ClientSecret = "secret",
                AppName = "Other",
                OrganizationId = 2,
                SubjectType = SubjectType.Account
            });
            context.SaveChanges();
            context.Organizations.Add(new Organization { Id = 1, Code = StgTenant, Name = "EcAuth Accounts (Staging)", TenantName = StgTenant });
            context.Clients.Add(new Client
            {
                ClientId = "ecauth-admin-console",
                ClientSecret = "secret",
                AppName = "EcAuth Accounts (Staging)",
                OrganizationId = 1,
                SubjectType = SubjectType.Account
            });
            context.SaveChanges();

            var result = await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(201, StatusOf(result));
            var owner = context.RedirectUris.AsNoTracking().Include(r => r.Client).Single(r => r.Uri == PreviewRedirectUri);
            Assert.Equal("ecauth-admin-console", owner.Client.ClientId);
        }

        [Fact]
        public async Task Register_Twice_IsIdempotent()
        {
            using var context = CreateContext();
            await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            var result = await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(200, StatusOf(result));
            Assert.Equal(2, Uris(context).Count);
        }

        [Theory]
        [InlineData("https://issue-159.ecauth-website-stg.pages.dev/mypage/")]
        [InlineData("https://issue-159.ecauth-website-stg.pages.dev/auth/callback?x=1")]
        [InlineData("https://issue-159.ecauth-website.pages.dev/auth/callback")]
        [InlineData("https://a.b.ecauth-website-stg.pages.dev/auth/callback")]
        [InlineData("http://issue-159.ecauth-website-stg.pages.dev/auth/callback")]
        [InlineData("https://evil.example/auth/callback")]
        [InlineData(FixedRedirectUri)]
        [InlineData(null)]
        public async Task Register_NonPreviewUri_Returns400(string? uri)
        {
            using var context = CreateContext();

            var result = await CreateController(context).Register(uri, CancellationToken.None);

            Assert.Equal(400, StatusOf(result));
            Assert.Equal(new[] { FixedRedirectUri }, Uris(context));
        }

        [Theory]
        [InlineData("Basic cHJldmlldy1vcHM6d3Jvbmc=")] // preview-ops:wrong
        [InlineData("Bearer preview-ops-secret")]
        [InlineData("Basic !!!")]
        [InlineData("")]
        public async Task Register_BadCredentials_Returns401(string authorization)
        {
            using var context = CreateContext();

            var controller = CreateController(context, authorization: authorization);
            var result = await controller.Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(401, StatusOf(result));
            Assert.Equal("Basic", controller.Response.Headers.WWWAuthenticate.ToString());
            Assert.Equal(new[] { FixedRedirectUri }, Uris(context));
        }

        [Fact]
        public async Task Register_WrongClientId_Returns401()
        {
            using var context = CreateContext();

            var result = await CreateController(context, authorization: Basic("someone", OpsClientSecret))
                .Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(401, StatusOf(result));
        }

        [Fact]
        public async Task Register_TenantWithoutCredentials_Returns404()
        {
            // accounts（live）には資格情報を配線しない。機能ごと見えない。
            using var context = CreateContext("accounts");

            var result = await CreateController(context, tenantName: "accounts")
                .Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(404, StatusOf(result));
        }

        [Fact]
        public async Task Register_NotConfigured_Returns404()
        {
            using var context = CreateContext();

            var result = await CreateController(context, configured: false)
                .Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(404, StatusOf(result));
        }

        [Fact]
        public async Task Register_OverLimit_Returns409()
        {
            using var context = CreateContext();
            var clientId = context.Clients.Single().Id;
            for (var i = 0; i < PreviewRedirectUriController.MaxPreviewRedirectUris; i++)
            {
                context.RedirectUris.Add(new RedirectUri
                {
                    ClientId = clientId,
                    Uri = $"https://pr-{i}.ecauth-website-stg.pages.dev/auth/callback"
                });
            }
            context.SaveChanges();

            var result = await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(409, StatusOf(result));
        }

        [Fact]
        public async Task Unregister_RemovesOnlyThatUri()
        {
            using var context = CreateContext();
            await CreateController(context).Register(PreviewRedirectUri, CancellationToken.None);

            var result = await CreateController(context).Unregister(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(204, StatusOf(result));
            Assert.Equal(new[] { FixedRedirectUri }, Uris(context));
        }

        [Fact]
        public async Task Unregister_Missing_Returns204()
        {
            using var context = CreateContext();

            var result = await CreateController(context).Unregister(PreviewRedirectUri, CancellationToken.None);

            Assert.Equal(204, StatusOf(result));
        }

        [Fact]
        public async Task Unregister_FixedUri_Returns400AndKeepsIt()
        {
            using var context = CreateContext();

            var result = await CreateController(context).Unregister(FixedRedirectUri, CancellationToken.None);

            Assert.Equal(400, StatusOf(result));
            Assert.Equal(new[] { FixedRedirectUri }, Uris(context));
        }
    }
}
