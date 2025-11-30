using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IdpUtilities;

namespace IdentityProvider.Services
{
    public class UserService : IUserService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<UserService> _logger;

        public UserService(EcAuthDbContext context, ILogger<UserService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<EcAuthUser> GetOrCreateUserAsync(IUserService.UserCreationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ExternalProvider))
                throw new ArgumentException("ExternalProvider cannot be null or empty.", nameof(request.ExternalProvider));
            if (string.IsNullOrWhiteSpace(request.ExternalSubject))
                throw new ArgumentException("ExternalSubject cannot be null or empty.", nameof(request.ExternalSubject));
            // EmailHashは空文字列を許可（メールが提供されない場合）
            if (request.EmailHash == null)
                throw new ArgumentException("EmailHash cannot be null.", nameof(request.EmailHash));
            if (request.OrganizationId <= 0)
                throw new ArgumentException("OrganizationId must be positive.", nameof(request.OrganizationId));

            // 既存ユーザーの検索（外部IdP情報で検索）
            var existingUser = await GetUserByExternalIdAsync(
                request.ExternalProvider, 
                request.ExternalSubject, 
                request.OrganizationId);

            if (existingUser != null)
            {
                // ユーザーが既存の場合、EmailHashを更新（変更されている可能性があるため）
                if (existingUser.EmailHash != request.EmailHash)
                {
                    existingUser.EmailHash = request.EmailHash;
                    existingUser.UpdatedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync();
                }
                return existingUser;
            }

            // 新規ユーザーの作成（JITプロビジョニング）
            var newUser = new EcAuthUser
            {
                Subject = Guid.NewGuid().ToString(),
                EmailHash = request.EmailHash,
                OrganizationId = request.OrganizationId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _context.EcAuthUsers.Add(newUser);

            // 外部IdPマッピングの作成
            var externalMapping = new ExternalIdpMapping
            {
                EcAuthSubject = newUser.Subject,
                ExternalProvider = request.ExternalProvider,
                ExternalSubject = request.ExternalSubject,
                CreatedAt = DateTimeOffset.UtcNow,
                EcAuthUser = newUser  // Navigation propertyを明示的に設定（EF Core 9マルチテナントフィルタ対応）
            };

            _context.ExternalIdpMappings.Add(externalMapping);

            await _context.SaveChangesAsync();

            return newUser;
        }

        public async Task<EcAuthUser?> GetUserBySubjectAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
                return null;

            return await _context.EcAuthUsers
                .Include(u => u.Organization)
                .Include(u => u.ExternalIdpMappings)
                .FirstOrDefaultAsync(u => u.Subject == subject);
        }

        public async Task<EcAuthUser?> GetUserByExternalIdAsync(string externalProvider, string externalSubject, int organizationId)
        {
            if (string.IsNullOrWhiteSpace(externalProvider) || string.IsNullOrWhiteSpace(externalSubject))
                return null;

            var mapping = await _context.ExternalIdpMappings
                .Include(m => m.EcAuthUser)
                .ThenInclude(u => u!.Organization)
                .Include(m => m.EcAuthUser)
                .ThenInclude(u => u!.ExternalIdpMappings)
                .FirstOrDefaultAsync(m => 
                    m.ExternalProvider == externalProvider && 
                    m.ExternalSubject == externalSubject &&
                    m.EcAuthUser!.OrganizationId == organizationId);

            return mapping?.EcAuthUser;
        }

        public async Task<EcAuthUser> CreateOrUpdateFromExternalAsync(ExternalUserInfo externalUser, int organizationId)
        {
            if (externalUser == null)
                throw new ArgumentNullException(nameof(externalUser));
            if (string.IsNullOrWhiteSpace(externalUser.Subject))
                throw new ArgumentException("ExternalUser.Subject cannot be null or empty.", nameof(externalUser));
            if (string.IsNullOrWhiteSpace(externalUser.Provider))
                throw new ArgumentException("ExternalUser.Provider cannot be null or empty.", nameof(externalUser));
            if (organizationId <= 0)
                throw new ArgumentException("OrganizationId must be positive.", nameof(organizationId));

            _logger.LogInformation("Creating or updating user from external IdP. Provider: {Provider}, Subject: {Subject}, OrganizationId: {OrganizationId}",
                externalUser.Provider, externalUser.Subject, organizationId);

            // メールアドレスのハッシュ化（メールが提供されている場合のみ）
            string emailHash = string.Empty;
            if (!string.IsNullOrWhiteSpace(externalUser.Email))
            {
                try
                {
                    emailHash = EmailHashUtil.HashEmail(externalUser.Email);
                    _logger.LogDebug("Email hash generated for user. Provider: {Provider}, Subject: {Subject}",
                        externalUser.Provider, externalUser.Subject);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to hash email for user. Provider: {Provider}, Subject: {Subject}",
                        externalUser.Provider, externalUser.Subject);
                    // メールハッシュ化に失敗した場合は空文字列のまま続行
                }
            }
            else
            {
                _logger.LogInformation("No email provided for user. Provider: {Provider}, Subject: {Subject}",
                    externalUser.Provider, externalUser.Subject);
            }

            // UserCreationRequestに変換
            var request = new IUserService.UserCreationRequest
            {
                ExternalProvider = externalUser.Provider,
                ExternalSubject = externalUser.Subject,
                EmailHash = emailHash,
                OrganizationId = organizationId
            };

            try
            {
                var result = await GetOrCreateUserAsync(request);
                
                _logger.LogInformation("Successfully created or updated user. EcAuthSubject: {EcAuthSubject}, Provider: {Provider}, ExternalSubject: {ExternalSubject}",
                    result.Subject, externalUser.Provider, externalUser.Subject);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create or update user from external IdP. Provider: {Provider}, Subject: {Subject}, OrganizationId: {OrganizationId}",
                    externalUser.Provider, externalUser.Subject, organizationId);
                throw;
            }
        }

        public async Task<List<EcAuthUser>> GetUsersByEmailHashAsync(string emailHash, int? organizationId = null)
        {
            if (string.IsNullOrWhiteSpace(emailHash))
            {
                _logger.LogDebug("Empty email hash provided to GetUsersByEmailHashAsync");
                return new List<EcAuthUser>();
            }

            try
            {
                var query = _context.EcAuthUsers
                    .Include(u => u.Organization)
                    .Include(u => u.ExternalIdpMappings)
                    .Where(u => u.EmailHash == emailHash);

                if (organizationId.HasValue)
                {
                    query = query.Where(u => u.OrganizationId == organizationId.Value);
                    _logger.LogDebug("Searching users by email hash for specific organization. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                        emailHash, organizationId.Value);
                }
                else
                {
                    _logger.LogDebug("Searching users by email hash across all organizations. EmailHash: {EmailHash}",
                        emailHash);
                }

                var users = await query.ToListAsync();

                _logger.LogInformation("Found {UserCount} users with email hash. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                    users.Count, emailHash, organizationId);

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve users by email hash. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                    emailHash, organizationId);
                throw;
            }
        }
    }
}