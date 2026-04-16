using IdentityProvider.Middlewares;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Middlewares
{
    public class TenantMiddlewareTests
    {
        private readonly Mock<ILogger<TenantMiddleware>> _mockLogger;

        public TenantMiddlewareTests()
        {
            _mockLogger = new Mock<ILogger<TenantMiddleware>>();
        }

        [Fact]
        public async Task InvokeAsync_PlatformPath_SkipsTenantResolution()
        {
            var tenantService = new MockTenantService();
            var nextCalled = false;
            RequestDelegate next = (context) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };

            var middleware = new TenantMiddleware(next, _mockLogger.Object);
            var context = new DefaultHttpContext();
            context.Request.Path = "/platform/v1/client-resolve";
            context.Request.Host = new HostString("tenant1.ec-auth.io");

            await middleware.InvokeAsync(context, tenantService);

            Assert.True(nextCalled);
            // テナント解決がスキップされたため、デフォルト値のまま
            Assert.Equal("test-tenant", tenantService.TenantName);
        }

        [Fact]
        public async Task InvokeAsync_PlatformRootPath_SkipsTenantResolution()
        {
            var tenantService = new MockTenantService();
            RequestDelegate next = (context) => Task.CompletedTask;

            var middleware = new TenantMiddleware(next, _mockLogger.Object);
            var context = new DefaultHttpContext();
            context.Request.Path = "/platform";
            context.Request.Host = new HostString("tenant1.ec-auth.io");

            await middleware.InvokeAsync(context, tenantService);

            Assert.Equal("test-tenant", tenantService.TenantName);
        }

        [Fact]
        public async Task InvokeAsync_NonPlatformPath_SetsTenant()
        {
            var tenantService = new MockTenantService();
            RequestDelegate next = (context) => Task.CompletedTask;

            var middleware = new TenantMiddleware(next, _mockLogger.Object);
            var context = new DefaultHttpContext();
            context.Request.Path = "/v1/token";
            context.Request.Host = new HostString("shop1.ec-auth.io");

            await middleware.InvokeAsync(context, tenantService);

            Assert.Equal("shop1", tenantService.TenantName);
        }

        [Fact]
        public async Task InvokeAsync_PlatformXPath_DoesNotSkipTenantResolution()
        {
            var tenantService = new MockTenantService();
            RequestDelegate next = (context) => Task.CompletedTask;

            var middleware = new TenantMiddleware(next, _mockLogger.Object);
            var context = new DefaultHttpContext();
            context.Request.Path = "/platformx/something";
            context.Request.Host = new HostString("shop1.ec-auth.io");

            await middleware.InvokeAsync(context, tenantService);

            // /platformx はセグメント境界にマッチしないため、通常のテナント解決が実行される
            Assert.Equal("shop1", tenantService.TenantName);
        }

        [Fact]
        public async Task InvokeAsync_PlatformNestedPath_SkipsTenantResolution()
        {
            var tenantService = new MockTenantService();
            RequestDelegate next = (context) => Task.CompletedTask;

            var middleware = new TenantMiddleware(next, _mockLogger.Object);
            var context = new DefaultHttpContext();
            context.Request.Path = "/platform/v1/signup/request";
            context.Request.Host = new HostString("shop1.ec-auth.io");

            await middleware.InvokeAsync(context, tenantService);

            Assert.Equal("test-tenant", tenantService.TenantName);
        }
    }
}
