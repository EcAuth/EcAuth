using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IdentityProvider.Services
{
    public class ExternalUserInfoService : IExternalUserInfoService
    {
        private readonly EcAuthDbContext _context;
        private readonly IExternalIdpTokenService _tokenService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ExternalUserInfoService> _logger;

        public ExternalUserInfoService(
            EcAuthDbContext context,
            IExternalIdpTokenService tokenService,
            IHttpClientFactory httpClientFactory,
            ILogger<ExternalUserInfoService> logger)
        {
            _context = context;
            _tokenService = tokenService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<IExternalUserInfoService.ExternalUserInfo?> GetExternalUserInfoAsync(
            IExternalUserInfoService.GetExternalUserInfoRequest request)
        {
            if (string.IsNullOrEmpty(request.EcAuthSubject))
                throw new ArgumentException("EcAuthSubject cannot be null or empty.", nameof(request.EcAuthSubject));
            if (string.IsNullOrEmpty(request.ExternalProvider))
                throw new ArgumentException("ExternalProvider cannot be null or empty.", nameof(request.ExternalProvider));

            try
            {
                // 1. 外部IdPマッピングの取得
                _logger.LogInformation("Fetching external IdP mapping for subject {Subject} and provider {Provider}",
                    request.EcAuthSubject, request.ExternalProvider);

                var externalMapping = await _context.ExternalIdpMappings
                    .IgnoreQueryFilters()
                    .Include(m => m.EcAuthUser)
                        .ThenInclude(u => u.Organization)
                    .FirstOrDefaultAsync(m => m.EcAuthSubject == request.EcAuthSubject &&
                                             m.ExternalProvider == request.ExternalProvider);

                if (externalMapping == null)
                {
                    _logger.LogWarning("External IdP mapping not found for subject {Subject} and provider {Provider}",
                        request.EcAuthSubject, request.ExternalProvider);
                    return null;
                }

                // 2. 外部IdPアクセストークンの取得
                _logger.LogInformation("Fetching external IdP access token for subject {Subject} and provider {Provider}",
                    request.EcAuthSubject, request.ExternalProvider);

                var externalToken = await _tokenService.GetTokenAsync(request.EcAuthSubject, request.ExternalProvider);

                if (externalToken == null)
                {
                    _logger.LogWarning("External IdP access token not found or expired for subject {Subject} and provider {Provider}",
                        request.EcAuthSubject, request.ExternalProvider);
                    return null;
                }

                // 3. OpenIdProvider設定の取得
                _logger.LogInformation("Fetching OpenIdProvider configuration for provider {Provider}",
                    request.ExternalProvider);

                var openIdProvider = await _context.OpenIdProviders
                    .FirstOrDefaultAsync(p => p.Name == request.ExternalProvider);

                if (openIdProvider == null)
                {
                    _logger.LogError("OpenIdProvider not found for provider {Provider}", request.ExternalProvider);
                    return null;
                }

                if (string.IsNullOrEmpty(openIdProvider.UserinfoEndpoint))
                {
                    _logger.LogError("UserInfo endpoint not configured for provider {Provider}", request.ExternalProvider);
                    return null;
                }

                // 4. 外部IdPのUserInfo endpointへのリクエスト
                _logger.LogInformation("Fetching user info from external IdP {Provider} at {Endpoint}",
                    request.ExternalProvider, openIdProvider.UserinfoEndpoint);

                var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", externalToken.AccessToken);

                var response = await httpClient.GetAsync(openIdProvider.UserinfoEndpoint);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch user info from external IdP {Provider}. Status: {StatusCode}, Reason: {Reason}",
                        request.ExternalProvider, response.StatusCode, response.ReasonPhrase);
                    return null;
                }

                var userInfoJson = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("Successfully fetched user info from external IdP {Provider}", request.ExternalProvider);

                // 5. ユーザー情報のパースと返却
                var userInfoDocument = JsonDocument.Parse(userInfoJson);

                return new IExternalUserInfoService.ExternalUserInfo
                {
                    UserInfoClaims = userInfoDocument,
                    ExternalProvider = request.ExternalProvider
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request error occurred while fetching external user info for provider {Provider}",
                    request.ExternalProvider);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON parsing error occurred while processing external user info for provider {Provider}",
                    request.ExternalProvider);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred in GetExternalUserInfoAsync for provider {Provider}",
                    request.ExternalProvider);
                return null;
            }
        }
    }
}
