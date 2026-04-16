using IdentityProvider.Controllers.Platform;
using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Controllers.Platform
{
    public class ClientResolveControllerTests : IDisposable
    {
        private readonly EcAuthDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly Mock<ILogger<ClientResolveController>> _mockLogger;
        private readonly ClientResolveController _controller;

        public ClientResolveControllerTests()
        {
            _context = TestDbContextHelper.CreateInMemoryContext();
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "PlatformApi:BaseDomain", "ec-auth.io" }
                })
                .Build();
            _mockLogger = new Mock<ILogger<ClientResolveController>>();
            _controller = new ClientResolveController(_context, _configuration, _mockLogger.Object);

            SeedTestData();
        }

        private void SeedTestData()
        {
            var organization = new Organization
            {
                Id = 1,
                Code = "shop1",
                Name = "ショップ1",
                TenantName = "shop1",
            };
            _context.Organizations.Add(organization);

            var client = new Client
            {
                Id = 1,
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AppName = "Test App",
                OrganizationId = 1,
            };
            _context.Clients.Add(client);

            var clientWithoutOrg = new Client
            {
                Id = 2,
                ClientId = "orphan-client-id",
                ClientSecret = "test-secret",
                AppName = "Orphan App",
                OrganizationId = null,
            };
            _context.Clients.Add(clientWithoutOrg);

            _context.SaveChanges();
        }

        [Fact]
        public async Task Resolve_ValidClientId_ReturnsOkWithTenantInfo()
        {
            var result = await _controller.Resolve("test-client-id");

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            var value = okResult.Value;
            var tenantName = value!.GetType().GetProperty("tenant_name")!.GetValue(value)!.ToString();
            var baseUrl = value.GetType().GetProperty("base_url")!.GetValue(value)!.ToString();
            var orgName = value.GetType().GetProperty("organization_name")!.GetValue(value)!.ToString();

            Assert.Equal("shop1", tenantName);
            Assert.Equal("https://shop1.ec-auth.io", baseUrl);
            Assert.Equal("ショップ1", orgName);
        }

        [Fact]
        public async Task Resolve_UnknownClientId_ReturnsNotFound()
        {
            var result = await _controller.Resolve("nonexistent-client-id");

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task Resolve_EmptyClientId_ReturnsBadRequest()
        {
            var result = await _controller.Resolve("");

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badRequestResult.StatusCode);
        }

        [Fact]
        public async Task Resolve_NullClientId_ReturnsBadRequest()
        {
            var result = await _controller.Resolve(null);

            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badRequestResult.StatusCode);
        }

        [Fact]
        public async Task Resolve_ClientWithoutOrganization_ReturnsNotFound()
        {
            var result = await _controller.Resolve("orphan-client-id");

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task Resolve_UsesConfiguredBaseDomain()
        {
            var customConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "PlatformApi:BaseDomain", "staging.ec-auth.io" }
                })
                .Build();
            var controller = new ClientResolveController(_context, customConfig, _mockLogger.Object);

            var result = await controller.Resolve("test-client-id");

            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value;
            var baseUrl = value!.GetType().GetProperty("base_url")!.GetValue(value)!.ToString();
            Assert.Equal("https://shop1.staging.ec-auth.io", baseUrl);
        }

        [Fact]
        public async Task Resolve_DefaultBaseDomainWhenNotConfigured()
        {
            var emptyConfig = new ConfigurationBuilder().Build();
            var controller = new ClientResolveController(_context, emptyConfig, _mockLogger.Object);

            var result = await controller.Resolve("test-client-id");

            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value;
            var baseUrl = value!.GetType().GetProperty("base_url")!.GetValue(value)!.ToString();
            Assert.Equal("https://shop1.ec-auth.io", baseUrl);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
