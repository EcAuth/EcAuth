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
                // 1. ユーザーと、そのユーザーが紐付いている OpenIdProvider を解決する（EcAuthDocs#122）
                _logger.LogInformation("Resolving external IdP link for subject {Subject} and provider {Provider}",
                    request.EcAuthSubject, request.ExternalProvider);

                var openIdProvider = await ResolveLinkedProviderAsync(request.EcAuthSubject, request.ExternalProvider);
                if (openIdProvider == null)
                {
                    _logger.LogWarning("External IdP link not found for subject {Subject} and provider {Provider}",
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

                if (string.IsNullOrEmpty(openIdProvider.UserinfoEndpoint))
                {
                    _logger.LogError("UserInfo endpoint not configured for provider {Provider}", request.ExternalProvider);
                    return null;
                }

                // 3. 外部IdPのUserInfo endpointへのリクエスト
                _logger.LogInformation("Fetching user info from external IdP {Provider} at {Endpoint}",
                    request.ExternalProvider, openIdProvider.UserinfoEndpoint);

                // 既定のクライアントはハンドラーをプールして Cookie を共有するため、Cookie を無効にした
                // 外部 IdP 用の名前付きクライアントを使う（AuthorizationCallbackController と同じ）。
                var httpClient = _httpClientFactory.CreateClient(Controllers.AuthorizationCallbackController.ExternalIdpHttpClientName);
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

                // 4. ユーザー情報のパースと返却
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

        /// <summary>
        /// ユーザーが紐付いている OpenIdProvider を、provider 名とユーザーのプール（Client / Organization）で解決する。
        ///
        /// <para>
        /// provider 名は Client 間で衝突しうるので、名前だけで OpenIdProvider を引かない（別 Client の
        /// userinfo エンドポイントへ別の IdP のトークンを送ってしまう）。紐付きの確認は
        /// <see cref="EcAuthUserIdentity"/>（issuer_key = idp:{open_id_provider.id}）を正とし、
        /// identity を持たない旧ユーザーだけ旧 <see cref="ExternalIdpMapping"/> を見る（expand 期間。
        /// 旧 mapping は Organization をまたぐ一意制約に当たる場合は書かれないため、identity だけを持つ
        /// ユーザーがいる）。
        /// </para>
        /// <para>
        /// テナントのクエリフィルターは使わない。アクセストークンの検証で subject は確定済みで、
        /// ログイン時の解決（UserService）と同じくユーザーのプールで明示的に絞る。
        /// </para>
        /// </summary>
        private async Task<OpenIdProvider?> ResolveLinkedProviderAsync(string ecAuthSubject, string providerName)
        {
            var user = await _context.EcAuthUsers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Subject == ecAuthSubject);
            if (user == null)
            {
                return null;
            }

            var query = _context.OpenIdProviders
                .IgnoreQueryFilters()
                .Where(p => p.Name == providerName
                    && p.Client != null
                    && p.Client.OrganizationId == user.OrganizationId);
            if (user.ClientId != null)
            {
                query = query.Where(p => p.Client!.ClientId == user.ClientId);
            }
            var candidates = await query.ToListAsync();
            if (candidates.Count == 0)
            {
                return null;
            }

            var issuerKeys = candidates.Select(p => B2CIssuerKey.ForIdp(p.Id)).ToList();
            var linkedIssuerKeys = await _context.EcAuthUserIdentities
                .IgnoreQueryFilters()
                .Where(i => i.EcAuthSubject == ecAuthSubject && issuerKeys.Contains(i.IssuerKey))
                .Select(i => i.IssuerKey)
                .ToListAsync();
            var linked = candidates.Where(p => linkedIssuerKeys.Contains(B2CIssuerKey.ForIdp(p.Id))).ToList();
            if (linked.Count == 1)
            {
                return linked[0];
            }
            if (linked.Count > 1)
            {
                // 1 Client に同名の provider が複数ある（設定の誤り）。どれか決められない
                _logger.LogWarning("Multiple linked providers share the name {Provider} for subject {Subject}", providerName, ecAuthSubject);
                return null;
            }

            // identity を持たない旧ユーザー（backfill で Client を決められず、まだ再ログインしていない）
            var hasLegacyMapping = await _context.ExternalIdpMappings
                .IgnoreQueryFilters()
                .AnyAsync(m => m.EcAuthSubject == ecAuthSubject && m.ExternalProvider == providerName);
            if (hasLegacyMapping && candidates.Count == 1)
            {
                return candidates[0];
            }
            return null;
        }
    }
}
