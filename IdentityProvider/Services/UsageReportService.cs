using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IUsageReportService"/>
    public class UsageReportService : IUsageReportService
    {
        /// <summary>
        /// 請求対象外とみなす Organization コードの接頭辞（staging 由来）。
        /// </summary>
        public const string NonBillableCodePrefix = "stg-";

        private readonly EcAuthDbContext _context;
        private readonly IB2BUserService _b2bUserService;

        public UsageReportService(EcAuthDbContext context, IB2BUserService b2bUserService)
        {
            _context = context;
            _b2bUserService = b2bUserService;
        }

        /// <inheritdoc />
        public async Task<IUsageReportService.UsageReport> GetReportAsync(
            IUsageReportService.UsageReportQuery query, CancellationToken cancellationToken = default)
        {
            var asOf = DateTimeOffset.UtcNow;
            if (query.OrganizationIds.Count == 0)
            {
                return new IUsageReportService.UsageReport(query.Month, asOf, Array.Empty<IUsageReportService.OrganizationUsage>());
            }

            // Organization 数に依存しない固定回数のクエリで組み立てる（N+1 にしない）。
            // 対象は顧客テナントなので全て IgnoreQueryFilters（論理削除済みも含めて引き、後段のポリシーで扱う）。
            var requestedIds = query.OrganizationIds.ToHashSet();
            var organizations = await _context.Organizations
                .IgnoreQueryFilters()
                .Where(o => requestedIds.Contains(o.Id))
                .OrderBy(o => o.Id)
                .ToListAsync(cancellationToken);

            // 記録時ではなく集計時に適用する除外ポリシー（EcAuthDocs#45 §2「除外の置き場」）。
            // 生データを中立に持つことで、ポリシーが変わっても遡って再計算できる。
            var billableByOrgId = organizations.ToDictionary(o => o.Id, IsBillable);
            if (!query.IncludeNonBillable)
            {
                organizations = organizations.Where(o => billableByOrgId[o.Id]).ToList();
            }

            var orgIds = organizations.Select(o => o.Id).ToHashSet();
            if (orgIds.Count == 0)
            {
                return new IUsageReportService.UsageReport(query.Month, asOf, Array.Empty<IUsageReportService.OrganizationUsage>());
            }

            // accounts コンソールの Client（SubjectType.Account）は課金対象外なので一覧にも出さない。
            var clients = await _context.Clients
                .IgnoreQueryFilters()
                .Where(c => c.OrganizationId != null
                    && orgIds.Contains(c.OrganizationId.Value)
                    && c.SubjectType != SubjectType.Account)
                .OrderBy(c => c.Id)
                .ToListAsync(cancellationToken);

            var yearMonth = query.Month.Value;
            var mauRows = _context.MonthlyActiveUsers
                .Where(m => m.YearMonth == yearMonth && orgIds.Contains(m.OrganizationId));

            // Client 別 MAU（請求値）。行は (year_month, client_id, subject) で一意なので Count() が distinct になる。
            var mauByClient = await mauRows
                .GroupBy(m => m.ClientId)
                .Select(g => new { ClientId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ClientId, x => x.Count, cancellationToken);

            // Organization 別 distinct MAU（参考値）。同一 subject が複数 Client で立っても 1 と数える。
            var mauByOrganization = await mauRows
                .GroupBy(m => m.OrganizationId)
                .Select(g => new { OrganizationId = g.Key, Count = g.Select(m => m.Subject).Distinct().Count() })
                .ToDictionaryAsync(x => x.OrganizationId, x => x.Count, cancellationToken);

            var registeredByClientId = await _b2bUserService.CountByClientsAsync(
                clients.Select(c => c.ClientId).ToList());

            var result = organizations.Select(o => new IUsageReportService.OrganizationUsage(
                OrganizationId: o.Id,
                Code: o.Code,
                Name: o.Name,
                IsSandbox: o.IsSandbox,
                IsBillable: billableByOrgId[o.Id],
                MonthlyActiveUsers: mauByOrganization.GetValueOrDefault(o.Id),
                Clients: clients
                    .Where(c => c.OrganizationId == o.Id)
                    .Select(c => new IUsageReportService.ClientUsage(
                        Id: c.Id,
                        ClientId: c.ClientId,
                        AppName: c.AppName,
                        SubjectType: c.SubjectType,
                        MonthlyActiveUsers: mauByClient.GetValueOrDefault(c.Id),
                        RegisteredB2BUsers: registeredByClientId.GetValueOrDefault(c.ClientId)))
                    .ToList()))
                .ToList();

            return new IUsageReportService.UsageReport(query.Month, asOf, result);
        }

        /// <summary>
        /// 請求対象の判定。サンドボックス / staging 由来（<c>stg-</c>）/ 論理削除済みは対象外。
        /// </summary>
        public static bool IsBillable(Organization organization)
        {
            return !organization.IsSandbox
                && organization.DeletedAt == null
                && !organization.Code.StartsWith(NonBillableCodePrefix, StringComparison.Ordinal);
        }
    }
}
