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
            var managed = await _context.AccountOrganizations
                .IgnoreQueryFilters()
                .Where(ao => ao.AccountSubject == subject)
                .Join(
                    _context.Organizations.IgnoreQueryFilters(),
                    ao => ao.OrganizationId,
                    o => o.Id,
                    (ao, o) => new IAccountService.ManagedOrganization(o.Id, o.Code, ao.Role))
                .ToListAsync();

            return managed;
        }
    }
}
