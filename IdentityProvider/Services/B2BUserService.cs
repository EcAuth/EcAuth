using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2Bユーザー管理サービスの実装
    /// </summary>
    public class B2BUserService : IB2BUserService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<B2BUserService> _logger;

        public B2BUserService(EcAuthDbContext context, ILogger<B2BUserService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IB2BUserService.CreateUserResult> CreateAsync(IB2BUserService.CreateUserRequest request)
        {
            // 入力検証
            ValidateCreateRequest(request);

            // Subject生成（UUID）- 指定された場合はバリデーション後にそのまま使用
            string subject;
            if (!string.IsNullOrWhiteSpace(request.Subject))
            {
                if (!Guid.TryParse(request.Subject, out _))
                    throw new ArgumentException("Subject は有効な UUID 形式である必要があります。", nameof(request));
                subject = request.Subject;
            }
            else
            {
                subject = Guid.NewGuid().ToString();
            }
            var now = DateTimeOffset.UtcNow;

            var user = new B2BUser
            {
                Subject = subject,
                ExternalId = request.ExternalId,
                UserType = request.UserType,
                OrganizationId = request.OrganizationId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.B2BUsers.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "B2Bユーザーを作成しました: Subject={Subject}, UserType={UserType}, OrganizationId={OrganizationId}",
                subject, request.UserType, request.OrganizationId);

            return new IB2BUserService.CreateUserResult
            {
                User = user
            };
        }

        /// <inheritdoc />
        public async Task<B2BUser?> GetBySubjectAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return null;
            }

            var user = await _context.B2BUsers
                .Include(u => u.Organization)
                .Include(u => u.PasskeyCredentials)
                .FirstOrDefaultAsync(u => u.Subject == subject);

            if (user == null)
            {
                _logger.LogDebug("B2Bユーザーが見つかりません: Subject={Subject}", subject);
            }

            return user;
        }

        /// <inheritdoc />
        public async Task<B2BUser?> GetByExternalIdAsync(string externalId, int organizationId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
            {
                return null;
            }

            var user = await _context.B2BUsers
                .Include(u => u.Organization)
                .Include(u => u.PasskeyCredentials)
                .FirstOrDefaultAsync(u => u.ExternalId == externalId && u.OrganizationId == organizationId);

            if (user == null)
            {
                _logger.LogDebug(
                    "B2Bユーザーが見つかりません: ExternalId={ExternalId}, OrganizationId={OrganizationId}",
                    externalId, organizationId);
            }

            return user;
        }

        /// <inheritdoc />
        public async Task<B2BUser?> UpdateAsync(IB2BUserService.UpdateUserRequest request)
        {
            // 入力検証
            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                throw new ArgumentException("Subject は必須です。", nameof(request));
            }

            var user = await _context.B2BUsers
                .FirstOrDefaultAsync(u => u.Subject == request.Subject);

            if (user == null)
            {
                _logger.LogDebug("更新対象のB2Bユーザーが見つかりません: Subject={Subject}", request.Subject);
                return null;
            }

            // 部分更新（nullでないフィールドのみ更新）
            if (request.ExternalId != null)
            {
                user.ExternalId = request.ExternalId;
            }

            if (request.UserType != null)
            {
                user.UserType = request.UserType;
            }

            user.UpdatedAt = DateTimeOffset.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("B2Bユーザーを更新しました: Subject={Subject}", request.Subject);

            return user;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return false;
            }

            var user = await _context.B2BUsers
                .FirstOrDefaultAsync(u => u.Subject == subject);

            if (user == null)
            {
                _logger.LogDebug("削除対象のB2Bユーザーが見つかりません: Subject={Subject}", subject);
                return false;
            }

            _context.B2BUsers.Remove(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("B2Bユーザーを削除しました: Subject={Subject}", subject);
            return true;
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return false;
            }

            return await _context.B2BUsers.AnyAsync(u => u.Subject == subject);
        }

        /// <inheritdoc />
        public async Task<int> CountByOrganizationAsync(int organizationId)
        {
            return await _context.B2BUsers
                .IgnoreQueryFilters()
                .CountAsync(u => u.OrganizationId == organizationId);
        }

        /// <summary>
        /// 作成リクエストの検証
        /// </summary>
        private static void ValidateCreateRequest(IB2BUserService.CreateUserRequest request)
        {
            if (request.OrganizationId <= 0)
            {
                throw new ArgumentException("OrganizationId は正の整数である必要があります。", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.UserType))
            {
                throw new ArgumentException("UserType は必須です。", nameof(request));
            }
        }
    }
}
