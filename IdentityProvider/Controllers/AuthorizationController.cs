using IdentityProvider.Filters;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdpUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Controllers
{
    [ServiceFilter(typeof(OrganizationFilter))]
    [Route("authorization")]
    [ApiController]
    public class AuthorizationController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IConfiguration _configuration;

        public AuthorizationController(EcAuthDbContext context, ITenantService tenantService, IConfiguration configuration)
        {
            _context = context;
            _tenantService = tenantService;
            _configuration = configuration;
        }

        [HttpGet]
        /// <summary>IdP の Authorization endpoint にリダイレクトします。</summary>
        /// <remarks>
        /// 必要なパラメータは以下のとおり
        /// - client.client_id
        /// - open_id_provider.name
        /// - redirect_uri
        /// パラメータで OpenID Provider を特定し、その IdP の Authorization endpoint にリダイレクトします。
        /// リダイレクト時に以下のパラメータを付与します。
        /// - client_id=<open_id_provider.client_id>
        /// - scope=<open_id_provider_scope.scope(スペース区切り)>
        /// - response_type=code
        /// - redirect_uri=<clientに登録したredirect_uri>
        /// - state=<state>
        /// </remarks>
        public async Task<IActionResult> Federate([FromQuery] string client_id, [FromQuery] string provider_name, [FromQuery] string redirect_uri)
        {
            Console.WriteLine(_tenantService.TenantName);
            var Client = await _context.Clients
                .Where(c => c.ClientId == client_id)
                .FirstOrDefaultAsync();
            var OpenIdProvider = await _context.OpenIdProviders
                .IgnoreQueryFilters()
                .Where(
                    p => p.Name == provider_name
                    && p.ClientId == Client.Id
                ).FirstOrDefaultAsync();
            var scopes = "openid email profile";
            if (OpenIdProvider.Name == "amazon-oauth2")
            {
                scopes = "profile postal_code profile:user_id";
            }
            var data = new State 
            { 
                OpenIdProviderId = OpenIdProvider.Id, 
                RedirectUri = redirect_uri,
                ClientId = Client.Id,
                OrganizationId = Client.OrganizationId ?? 0,
                Scope = scopes
            };
            var password = Environment.GetEnvironmentVariable("STATE_PASSWORD");
            var options = new Iron.Options();

            var sealedData = await Iron.Seal<State>(data, password, options);
            Console.WriteLine($"Sealed Data: {sealedData}");
            var unsealedData = await Iron.Unseal<State>(sealedData, password, options);
            Console.WriteLine($"Unsealed Data: {unsealedData}");
            // AuthorizationEndpoint に既存のクエリパラメータが含まれている場合は & で連結
            var separator = OpenIdProvider.AuthorizationEndpoint?.Contains("?") == true ? "&" : "?";
            return Redirect(
                $"{OpenIdProvider.AuthorizationEndpoint}" +
                $"{separator}client_id={OpenIdProvider.IdpClientId}" +
                $"&scope={Uri.EscapeDataString(scopes)}" +
                $"&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(_configuration["DEFAULT_ORGANIZATION_REDIRECT_URI"] ?? "https://localhost:8081/auth/callback")}" +
                $"&state={Uri.EscapeDataString(sealedData)}"
             );
        }
    }
}
