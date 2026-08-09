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

            // 論理削除済み Organization の Client は解決しない。プラグインは接続先 IdP の
            // base_url をこのエンドポイントから得るため、ここで 404 にすることが
            // 「サイトを削除したらプラグインからの認証が止まる」の実効的な担保になる。
            // Client 自体にはクエリフィルターが無く、ここも IgnoreQueryFilters で引いているため、
            // Organization 側の DeletedAt 条件は明示的に書く必要がある。
            var result = await _context.Clients
                .IgnoreQueryFilters()
                .Where(c => c.ClientId == client_id)
                .Select(c => new
                {
                    TenantName = c.Organization != null && c.Organization.DeletedAt == null
                        ? c.Organization.TenantName
                        : null,
                    OrganizationName = c.Organization != null ? c.Organization.Name : null,
                })
                .FirstOrDefaultAsync();

            if (result == null || result.TenantName == null)
            {
                _logger.LogWarning("Client not found, has no organization, or organization is deleted: client_id={ClientId}", client_id);
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
