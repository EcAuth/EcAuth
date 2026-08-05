using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IAccountService" />
    public class AccountService : IAccountService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<AccountService> _logger;

        public AccountService(EcAuthDbContext context, ILogger<AccountService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<Account?> GetBySubjectAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return null;
            }

            var account = await _context.Accounts
                .Include(a => a.Organization)
                .FirstOrDefaultAsync(a => a.Subject == subject);

            if (account == null)
            {
                _logger.LogDebug("Account が見つかりません: Subject={Subject}", subject);
            }

            return account;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<IAccountService.ManagedOrganization>> GetManagedOrganizationsAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return Array.Empty<IAccountService.ManagedOrganization>();
            }

            // account_organization はテナント横断（クエリフィルター対象外）。
            // 管理対象 Organization も別テナントのため、Organization 側も IgnoreQueryFilters で引く。
            //
            // IgnoreQueryFilters は Organization のグローバルクエリフィルターごと外すため、
            // 論理削除の条件も一緒に外れる。削除済みサイトが managed_orgs クレームに残ると
            // 削除後もそのテナントのトークンが発行できてしまうため、ここで明示的に除外する。
            var managed = await _context.AccountOrganizations
                .IgnoreQueryFilters()
                .Where(ao => ao.AccountSubject == subject)
                .Join(
                    _context.Organizations.IgnoreQueryFilters().Where(o => o.DeletedAt == null),
                    ao => ao.OrganizationId,
                    o => o.Id,
                    (ao, o) => new IAccountService.ManagedOrganization(o.Id, o.Code, ao.Role))
                .ToListAsync();

            return managed;
        }
    }
}
