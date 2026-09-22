using IdentityProvider.Models;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 利用状況（MAU / 登録ユーザー数）の参照集計（EcAuthDocs#45）。
    ///
    /// マイページの <c>GET /v1/account/usage</c> と ConsoleApp の <c>usage-report</c> が**同じ実装**を通ることで、
    /// 顧客に見える数字と請求に使う数字が構造的に一致する。集計ロジックはここ以外に書かないこと。
    ///
    /// <para>
    /// <c>monthly_active_user</c> にはテナントクエリフィルターが無い（accounts テナントから顧客 Organization を
    /// 横断参照するため）。代わりに <see cref="UsageReportQuery.OrganizationIds"/> を必須にして、呼び出し側が
    /// 参照してよい Organization を明示する構造で守る。
    /// </para>
    /// </summary>
    public interface IUsageReportService
    {
        /// <param name="Month">対象月（JST）</param>
        /// <param name="OrganizationIds">集計対象の Organization。空なら DB を引かずに空を返す</param>
        /// <param name="IncludeNonBillable">
        /// true ならサンドボックス / <c>stg-</c> / 論理削除済みの Organization も <see cref="OrganizationUsage.IsBillable"/> = false
        /// を付けて返す（マイページ向け）。false なら請求対象だけに絞る（請求作業向け）
        /// </param>
        public sealed record UsageReportQuery(UsageMonth Month, IReadOnlyCollection<int> OrganizationIds, bool IncludeNonBillable);

        /// <param name="Month">対象月</param>
        /// <param name="AsOf">
        /// <see cref="ClientUsage.RegisteredB2BUsers"/> のスナップショット時刻（UTC）。登録ユーザー数は「その月の数」ではなく
        /// 問い合わせ時点の値なので、必ず併記する
        /// </param>
        public sealed record UsageReport(UsageMonth Month, DateTimeOffset AsOf, IReadOnlyList<OrganizationUsage> Organizations);

        /// <param name="MonthlyActiveUsers">
        /// Organization 単位の distinct MAU（参考値）。<see cref="Clients"/> の合計との差が「同一人物の複数 Client での
        /// 多重カウント」を示す。請求には使わない
        /// </param>
        /// <param name="IsBillable">請求対象か（サンドボックス / <c>stg-</c> / 論理削除済みは false）</param>
        public sealed record OrganizationUsage(
            int OrganizationId,
            string Code,
            string Name,
            bool IsSandbox,
            bool IsBillable,
            int MonthlyActiveUsers,
            IReadOnlyList<ClientUsage> Clients);

        /// <param name="Id"><c>client.id</c></param>
        /// <param name="ClientId"><c>client.client_id</c>（文字列）</param>
        /// <param name="MonthlyActiveUsers">Client 単位の MAU。<b>請求に使う値</b></param>
        /// <param name="RegisteredB2BUsers">
        /// <c>b2b_user_identity</c> を <c>client_id</c> で数えた登録ユーザー数（参考値、<see cref="UsageReport.AsOf"/> 時点）。
        /// B2BUser は物理削除されるため <c>MonthlyActiveUsers &gt; RegisteredB2BUsers</c> は正常に起こりうる
        /// </param>
        public sealed record ClientUsage(
            int Id,
            string ClientId,
            string AppName,
            SubjectType SubjectType,
            int MonthlyActiveUsers,
            int RegisteredB2BUsers);

        /// <summary>
        /// 指定 Organization 群の利用状況を集計する。MAU 0 の Client も返す（一覧を全部出せるように）。
        /// <see cref="SubjectType.Account"/> の Client（accounts コンソール）は含めない。
        /// </summary>
        Task<UsageReport> GetReportAsync(UsageReportQuery query, CancellationToken cancellationToken = default);
    }
}
