using MockOpenIdProvider.Services;
using Microsoft.EntityFrameworkCore;
using MockOpenIdProvider.Models;

namespace MockOpenIdProvider.Middlewares
{
    public class OrganizationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<OrganizationMiddleware> _logger;

        public OrganizationMiddleware(RequestDelegate next, ILogger<OrganizationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IOrganizationService organizationService, IdpDbContext dbContext)
        {
            // クエリパラメータ ?org= から取得
            var orgFromQuery = context.Request.Query["org"].FirstOrDefault();

            // ヘッダー X-Organization からフォールバック取得
            var orgFromHeader = context.Request.Headers["X-Organization"].FirstOrDefault();

            // デフォルト値: dev
            var tenantName = orgFromQuery ?? orgFromHeader ?? "dev";

            _logger.LogInformation("Organization resolution: QueryParam={QueryParam}, Header={Header}, FinalTenant={FinalTenant}",
                orgFromQuery, orgFromHeader, tenantName);

            // Organization の存在確認
            var organization = await dbContext.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantName == tenantName);

            if (organization == null)
            {
                _logger.LogWarning("Invalid organization: {TenantName}", tenantName);
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "invalid_organization",
                    error_description = $"Organization '{tenantName}' not found"
                });
                return;
            }

            organizationService.SetTenantName(tenantName);

            await _next(context);
        }
    }
}
