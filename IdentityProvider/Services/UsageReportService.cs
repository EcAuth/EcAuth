using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IUsageReportService"/>
    public class UsageReportService : IUsageReportService
    {
        /// <summary>
        /// EcAuth 自身の管理用 Organization のコード（<c>AccountsOrganizationSeeder</c> の定義と一致）。
        /// 顧客ではないので請求対象にしない。
        ///
        /// <para>
        /// コードの接頭辞（<c>stg-</c> 等）では判定しない。組織コードは申込ホストから導出されるため
        /// （<c>OrganizationProvisioningService.DeriveOrganizationCode</c>）、本番 URL が <c>stg.example.jp</c> の顧客は
        /// <c>stg-example-jp</c> になり、接頭辞で弾くと請求から静かに落ちる。組織コードはグローバル一意なので、
        /// 顧客がこの一覧の値を取ることはない。
        /// </para>
        /// </summary>
        public static readonly IReadOnlySet<string> InternalOrganizationCodes =
            new HashSet<string>(StringComparer.Ordinal) { "accounts", "stg-accounts" };

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
            var billableByOrgId = organizations.ToDictionary(o => o.Id, o => IsBillable(o, query.Month));
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
        /// 対象月における請求対象の判定。サンドボックスと EcAuth 自身の管理用 Organization は常に対象外。
        ///
        /// <para>
        /// 論理削除は「<b>対象月に 1 瞬でも有効だったか</b>」（= 削除が対象月の開始より後か）で見る。現在の削除状態で弾くと、
        /// 月の途中で解約したサイトの当月 MAU（解約前に発生した請求可能な利用）が請求から丸ごと落ちる。
        /// 請求は月末締め・翌月 10 日（EcAuthDocs#119）なので、集計時点では必ず「削除済み」に見える。
        /// <c>Organization.DeletedAt</c> が物理削除をしない理由（解約済みサイトも期間つきで残す）もこれ。
        /// </para>
        /// <para>
        /// なお削除済み Organization は認証系の全経路から除外されるため、削除後の月に MAU 行が立つことはない。
        /// 結果としてこの判定は「MAU 行のある月は請求対象」と一致する。
        /// </para>
        /// </summary>
        public static bool IsBillable(Organization organization, UsageMonth month)
        {
            return !organization.IsSandbox
                && !InternalOrganizationCodes.Contains(organization.Code)
                && (organization.DeletedAt == null || organization.DeletedAt > month.Start);
        }
    }
}
