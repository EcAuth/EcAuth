using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    public class ExternalIdpTokenService : IExternalIdpTokenService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<ExternalIdpTokenService> _logger;

        public ExternalIdpTokenService(EcAuthDbContext context, ILogger<ExternalIdpTokenService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ExternalIdpToken> SaveTokenAsync(IExternalIdpTokenService.SaveTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.EcAuthSubject))
                throw new ArgumentException("EcAuthSubject cannot be null or empty.", nameof(request.EcAuthSubject));
            if (string.IsNullOrEmpty(request.ExternalProvider))
                throw new ArgumentException("ExternalProvider cannot be null or empty.", nameof(request.ExternalProvider));
            if (string.IsNullOrEmpty(request.AccessToken))
                throw new ArgumentException("AccessToken cannot be null or empty.", nameof(request.AccessToken));

            // 既存のトークンを検索（ユニーク制約: ecauth_subject + external_provider）
            var existingToken = await _context.ExternalIdpTokens
                .FirstOrDefaultAsync(t => t.EcAuthSubject == request.EcAuthSubject &&
                                         t.ExternalProvider == request.ExternalProvider);

            if (existingToken != null)
            {
                // 既存のトークンを更新
                existingToken.AccessToken = request.AccessToken;
                existingToken.RefreshToken = request.RefreshToken;
                existingToken.ExpiresAt = request.ExpiresAt;
                existingToken.UpdatedAt = DateTimeOffset.UtcNow;

                _context.ExternalIdpTokens.Update(existingToken);
                await _context.SaveChangesAsync();

                _logger.LogInformation("External IdP token updated for user {Subject} and provider {Provider}",
                    request.EcAuthSubject, request.ExternalProvider);

                return existingToken;
            }
            else
            {
                // 新しいトークンを作成
                var newToken = new ExternalIdpToken
                {
                    EcAuthSubject = request.EcAuthSubject,
                    ExternalProvider = request.ExternalProvider,
                    AccessToken = request.AccessToken,
                    RefreshToken = request.RefreshToken,
                    ExpiresAt = request.ExpiresAt,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                _context.ExternalIdpTokens.Add(newToken);
                await _context.SaveChangesAsync();

                _logger.LogInformation("External IdP token saved for user {Subject} and provider {Provider}",
                    request.EcAuthSubject, request.ExternalProvider);

                return newToken;
            }
        }

        public async Task<ExternalIdpToken?> GetTokenAsync(string ecAuthSubject, string externalProvider)
        {
            if (string.IsNullOrEmpty(ecAuthSubject))
                throw new ArgumentException("EcAuthSubject cannot be null or empty.", nameof(ecAuthSubject));
            if (string.IsNullOrEmpty(externalProvider))
                throw new ArgumentException("ExternalProvider cannot be null or empty.", nameof(externalProvider));

            var token = await _context.ExternalIdpTokens
                .FirstOrDefaultAsync(t => t.EcAuthSubject == ecAuthSubject &&
                                         t.ExternalProvider == externalProvider);

            if (token == null)
            {
                _logger.LogDebug("External IdP token not found for user {Subject} and provider {Provider}",
                    ecAuthSubject, externalProvider);
                return null;
            }

            if (token.IsExpired)
            {
                _logger.LogWarning("External IdP token expired for user {Subject} and provider {Provider}",
                    ecAuthSubject, externalProvider);
                return null;
            }

            _logger.LogDebug("External IdP token retrieved for user {Subject} and provider {Provider}",
                ecAuthSubject, externalProvider);

            return token;
        }

        public async Task<ExternalIdpToken?> RefreshTokenAsync(string ecAuthSubject, string externalProvider)
        {
            if (string.IsNullOrEmpty(ecAuthSubject))
                throw new ArgumentException("EcAuthSubject cannot be null or empty.", nameof(ecAuthSubject));
            if (string.IsNullOrEmpty(externalProvider))
                throw new ArgumentException("ExternalProvider cannot be null or empty.", nameof(externalProvider));

            var token = await _context.ExternalIdpTokens
                .FirstOrDefaultAsync(t => t.EcAuthSubject == ecAuthSubject &&
                                         t.ExternalProvider == externalProvider);

            if (token == null)
            {
                _logger.LogWarning("External IdP token not found for refresh: user {Subject}, provider {Provider}",
                    ecAuthSubject, externalProvider);
                return null;
            }

            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                _logger.LogWarning("Refresh token not available for user {Subject} and provider {Provider}",
                    ecAuthSubject, externalProvider);
                return null;
            }

            // TODO: Phase 2で実装
            // 外部IdPのトークンエンドポイントにリフレッシュトークンを送信
            // 新しいアクセストークンを取得して保存
            _logger.LogWarning("RefreshTokenAsync is not yet implemented (Phase 2 feature)");

            return null;
        }

        public async Task<int> CleanupExpiredTokensAsync()
        {
            var now = DateTimeOffset.UtcNow;

            var expiredTokens = await _context.ExternalIdpTokens
                .Where(t => t.ExpiresAt <= now)
                .ToListAsync();

            if (expiredTokens.Count == 0)
            {
                _logger.LogDebug("No expired external IdP tokens found");
                return 0;
            }

            _context.ExternalIdpTokens.RemoveRange(expiredTokens);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Cleaned up {Count} expired external IdP tokens", expiredTokens.Count);

            return expiredTokens.Count;
        }
    }
}
