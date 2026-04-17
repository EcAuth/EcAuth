using IdentityProvider.Constants;
using IdentityProvider.Models;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Controllers.Platform
{
    [Route(PlatformApiConstants.RoutePrefix + "/v{version:apiVersion}/client-resolve")]
    [ApiController]
    [ApiVersion("1.0")]
    public class ClientResolveController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ClientResolveController> _logger;

        public ClientResolveController(
            EcAuthDbContext context,
            IConfiguration configuration,
            ILogger<ClientResolveController> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Resolve([FromQuery] string? client_id)
        {
            if (string.IsNullOrWhiteSpace(client_id))
            {
                return BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "client_id パラメータは必須です。"
                });
            }

            var result = await _context.Clients
                .IgnoreQueryFilters()
                .Where(c => c.ClientId == client_id)
                .Select(c => new
                {
                    TenantName = c.Organization != null ? c.Organization.TenantName : null,
                    OrganizationName = c.Organization != null ? c.Organization.Name : null,
                })
                .FirstOrDefaultAsync();

            if (result == null || result.TenantName == null)
            {
                _logger.LogWarning("Client not found or has no organization: client_id={ClientId}", client_id);
                return NotFound(new
                {
                    error = "not_found",
                    error_description = "指定された client_id に対応するテナントが見つかりません。"
                });
            }

            var baseDomain = _configuration["PlatformApi:BaseDomain"] ?? "ec-auth.io";
            var baseUrl = $"https://{result.TenantName}.{baseDomain}";

            _logger.LogInformation("Client resolved: client_id={ClientId}, tenant={TenantName}", client_id, result.TenantName);

            return Ok(new
            {
                tenant_name = result.TenantName,
                base_url = baseUrl,
                organization_name = result.OrganizationName
            });
        }
    }
}
