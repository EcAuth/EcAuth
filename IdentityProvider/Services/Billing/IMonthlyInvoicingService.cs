using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// 月次請求（EcAuthDocs#119 PR-2）。前月分を Account ごとに Stripe の Invoice（下書き）として起票し、
    /// 起票しなかった Account も理由付きで <see cref="BillingInvoice"/> に記録する。
    /// <para>
    /// 金額は見込み額と同じ <see cref="IBillingService.GetEstimateAsync"/>（= <c>BuildEstimate</c>）で出す。
    /// 請求先は <c>account_organization.role = owner</c> の Account。確定とカードへの請求は、起票時に指定した
    /// <c>automatically_finalizes_at</c> に Stripe が行う（EcAuth は起票するだけ）。
    /// </para>
    /// <para>
    /// 二重起票は <c>billing_invoice</c> の <c>UNIQUE(year_month, account_subject)</c> と Stripe の冪等キーで防ぐ。
    /// 行は Stripe を呼ぶ前に INSERT し（金額と内訳はその時点で固定）、途中で落ちた行は
    /// <see cref="Statuses.Pending"/> のまま残って次の実行で続きから再開する。
    /// </para>
    /// </summary>
    public interface IMonthlyInvoicingService
    {
        /// <summary><see cref="BillingInvoice.Status"/> の値。</summary>
        public static class Statuses
        {
            /// <summary>行を確保したが Stripe での起票が終わっていない（実行中、または途中で失敗した）。次の実行で再開する</summary>
            public const string Pending = "pending";
            /// <summary>Stripe に下書きで起票済み。<c>finalizes_at</c> に Stripe が確定して請求する</summary>
            public const string Draft = "draft";
            public const string Open = "open";
            public const string Paid = "paid";
            public const string Void = "void";
            public const string Uncollectible = "uncollectible";
            /// <summary>下書きを削除した（確定前の取り消し）</summary>
            public const string Deleted = "deleted";
            /// <summary>請求額が 0 円（無料枠内・初月無料・請求対象外の Client だけ）</summary>
            public const string SkippedZero = "skipped_zero";
            /// <summary>Account 全体が請求対象外（<c>account_billing_plan.billing_exempt</c>）</summary>
            public const string SkippedExempt = "skipped_exempt";
            /// <summary>請求額はあるが支払い方法が未登録。請求書は作らず金額だけ記録する（EcAuthDocs#119 の決定）</summary>
            public const string SkippedNoPaymentMethod = "skipped_no_payment_method";

            public static bool IsSkipped(string status) =>
                status is SkippedZero or SkippedExempt or SkippedNoPaymentMethod;
        }

        /// <summary>
        /// 対象の絞り込み。どちらも null なら全テナント・全 Account。
        /// </summary>
        public sealed record Scope(string? TenantName = null, string? AccountSubject = null)
        {
            public static readonly Scope All = new();
        }

        /// <summary>起票する（下書きを作る）判断。<see cref="AccountPlan.Decision"/> の値。</summary>
        public const string DecisionInvoice = "invoice";

        /// <summary>
        /// 1 Account の起票内容（dry-run の出力、または実行前の判断）。
        /// </summary>
        /// <param name="Decision"><see cref="DecisionInvoice"/> または skipped_* の状態</param>
        /// <param name="Existing">既に記録がある場合はその行（再実行しても変わらない）</param>
        public sealed record AccountPlan(
            string AccountSubject,
            string TenantName,
            string Email,
            string? StripeCustomerId,
            bool PaymentMethodRegistered,
            string Decision,
            IBillingService.Estimate Estimate,
            BillingInvoice? Existing);

        /// <param name="Invoiced">今回 Stripe に起票した（または再開して起票し終えた）件数</param>
        /// <param name="Skipped">今回 skipped_* で記録した件数</param>
        /// <param name="AlreadyProcessed">既に記録があり何もしなかった件数</param>
        /// <param name="Failed">失敗した件数（pending のまま残り、次の実行で再開する）</param>
        public sealed record RunResult(UsageMonth Month, int Invoiced, int Skipped, int AlreadyProcessed, int Failed);

        /// <summary>
        /// 起票内容を出すだけで何も書き込まない（ConsoleApp <c>billing-invoices --dry-run</c>）。
        /// </summary>
        Task<IReadOnlyList<AccountPlan>> PlanAsync(UsageMonth month, Scope scope, CancellationToken cancellationToken);

        /// <summary>
        /// 対象月を起票する。既に記録のある Account は飛ばし、pending の行は続きから再開する。
        /// Account ごとの失敗は数えて続行する（1 件の失敗で全体を止めない）。
        /// </summary>
        Task<RunResult> RunAsync(UsageMonth month, Scope scope, CancellationToken cancellationToken);

        /// <summary>
        /// 起票を取り消す（確定前は下書きの削除、確定後で未払いは void）。記録の行は状態を変えて残す。
        /// </summary>
        Task<BillingInvoice> CancelAsync(UsageMonth month, string accountSubject, CancellationToken cancellationToken);

        /// <summary>
        /// 月次ジョブの 1 回分（<see cref="MonthlyInvoicingHostedService"/> が定期的に呼ぶ）。
        /// <see cref="DueMonth"/> が対象月を返し、その月の実行（<see cref="BillingInvoiceRun"/>）が未完了なら
        /// <see cref="RunAsync"/> する。何もしなかったら null。
        /// </summary>
        Task<RunResult?> RunIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

        /// <summary>起票時刻（毎月 1 日の JST 03:00）。前月分の MAU が確定してから起票する。</summary>
        public static readonly TimeSpan InvoicingTimeOfDay = TimeSpan.FromHours(3);

        /// <summary>
        /// <paramref name="now"/> の時点で自動起票すべき月。当月 1 日の JST 03:00 を過ぎていれば前月、
        /// それより前、または前月が <paramref name="startMonth"/> より前なら null。
        /// 自動で追うのは前月だけ（それより古い月の取りこぼしは ConsoleApp で手当てする。黙って過去月を
        /// まとめて起票しない）。
        /// </summary>
        public static UsageMonth? DueMonth(DateTimeOffset now, UsageMonth startMonth)
        {
            var current = UsageMonth.FromInstant(now);
            if (now < current.Start + InvoicingTimeOfDay)
            {
                return null;
            }
            var previous = UsageMonth.FromInstant(current.Start.AddMonths(-1));
            return previous < startMonth ? null : previous;
        }
    }
}
