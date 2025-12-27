using IdentityProvider.Services;
using System.Net;

namespace IdentityProvider.Middlewares
{
    public class TenantMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TenantMiddleware> _logger;

        public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, ITenantService tenantService)
        {
            var host = context.Request.Host.Host;
            var tenantName = ExtractTenantNameFromHost(host);
            var defaultOrganizationTenantName = Environment.GetEnvironmentVariable("DEFAULT_ORGANIZATION_TENANT_NAME") ?? string.Empty;

            var finalTenantName = string.IsNullOrEmpty(tenantName) ? defaultOrganizationTenantName : tenantName;

            _logger.LogInformation("Tenant resolution: Host={Host}, ExtractedTenant={ExtractedTenant}, DefaultTenant={DefaultTenant}, FinalTenant={FinalTenant}",
                host, tenantName, defaultOrganizationTenantName, finalTenantName);

            tenantService.SetTenant(finalTenantName);

            await _next(context);
        }

        private string ExtractTenantNameFromHost(string host)
        {
            // IPアドレスの場合は空文字列を返す（デフォルトテナントにフォールバック）
            if (IPAddress.TryParse(host, out _))
            {
                return string.Empty;
            }

            // localhost の場合も空文字列を返す（デフォルトテナントにフォールバック）
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Azure プラットフォームドメインの場合は空文字列を返す（デフォルトテナントにフォールバック）
            // これらのドメインはテナント名を含まないため除外する
            if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // サブドメイン形式（tenant.example.com）からテナント名を抽出
            var segments = host.Split('.');
            if (segments.Length > 2)
            {
                return segments[0];
            }

            return string.Empty;
        }
    }
}
