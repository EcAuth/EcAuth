using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// マイページ向けの課金操作（EcAuthDocs#119）: 支払い方法の登録・管理と当月の見込み額、Stripe Webhook の反映。
    /// 支払い主体は Account、集計単位は Client。見込み額は <see cref="IUsageReportService"/> と
    /// <see cref="IPricingPlanResolver"/> / <see cref="IPricingCalculator"/> を通し、月次の Invoice 起票と同じ数字になる。
    /// </summary>
    public interface IBillingService
    {
        /// <summary>Client が請求対象外になる理由。レスポンスの <c>exempt_reason</c> にそのまま出す。</summary>
        public static class ExemptReasons
        {
            /// <summary>Account の課金設定で「課金しない」（<see cref="AccountBillingPlan.BillingExempt"/>）</summary>
            public const string AccountExempt = "account_exempt";
            /// <summary>Organization が請求対象外（サンドボックス / 内部 / 対象月前に削除済み）</summary>
            public const string OrganizationNotBillable = "organization_not_billable";
            /// <summary>Client の課金設定で「課金しない」（<see cref="Client.BillingExempt"/>）</summary>
            public const string ClientExempt = "client_exempt";
            /// <summary>Client を作成した月（初月無料）</summary>
            public const string FirstMonth = "first_month";
        }

        /// <param name="Id"><c>client.id</c></param>
        /// <param name="MonthlyActiveUsers">Client の当月 MAU</param>
        /// <param name="FreeTierMau">無料枠（適用した料金表）</param>
        /// <param name="BillableUnits">無料枠を超えた MAU</param>
        /// <param name="ListPriceJpy">料金表どおりの金額（円、税込、割引前）。請求対象外でも計算して見せる</param>
        /// <param name="AmountJpy">請求見込み（円、税込、割引前）。請求対象外なら 0</param>
        /// <param name="IsBillable">この Client の当月分を請求するか</param>
        /// <param name="ExemptReason">請求しない理由（<see cref="ExemptReasons"/>）。請求対象なら null</param>
        /// <param name="CustomPricing">Account の独自料金表で計算したか</param>
        public sealed record ClientEstimate(
            int Id,
            string ClientId,
            string AppName,
            SubjectType SubjectType,
            int MonthlyActiveUsers,
            int FreeTierMau,
            int BillableUnits,
            long ListPriceJpy,
            long AmountJpy,
            bool IsBillable,
            string? ExemptReason,
            bool CustomPricing);

        /// <param name="IsBillable">Organization として請求対象か（サンドボックス / 内部 / 対象月前に削除済みは false）</param>
        /// <param name="AmountJpy">配下 Client の <see cref="ClientEstimate.AmountJpy"/> の和（割引前）</param>
        public sealed record OrganizationEstimate(
            int OrganizationId,
            string Code,
            string Name,
            bool IsSandbox,
            bool IsBillable,
            long AmountJpy,
            IReadOnlyList<ClientEstimate> Clients);

        /// <param name="Exempt">Account 全体が課金対象外か</param>
        /// <param name="DiscountPercent">割引率（設定時）</param>
        /// <param name="DiscountJpy">定額割引（設定時）</param>
        /// <param name="CustomB2BPricing">B2B の独自料金表が設定されているか</param>
        /// <param name="CustomB2CPricing">B2C の独自料金表が設定されているか</param>
        public sealed record PlanSummary(
            bool Exempt,
            int? DiscountPercent,
            long? DiscountJpy,
            bool CustomB2BPricing,
            bool CustomB2CPricing);

        /// <param name="Month">対象月</param>
        /// <param name="AsOf">集計時刻（UTC）</param>
        /// <param name="SubtotalJpy">請求対象 Client の合計（割引前）</param>
        /// <param name="DiscountJpy">割引額（正の値）。請求書では負の 1 行</param>
        /// <param name="TotalJpy"><c>SubtotalJpy - DiscountJpy</c>（0 以上）</param>
        public sealed record Estimate(
            UsageMonth Month,
            DateTimeOffset AsOf,
            long SubtotalJpy,
            long DiscountJpy,
            long TotalJpy,
            PlanSummary Plan,
            IReadOnlyList<OrganizationEstimate> Organizations);

        /// <param name="HasStripeCustomer">Stripe Customer を作成済みか</param>
        /// <param name="PaymentMethodRegisteredAt">既定の支払い方法が登録された時刻。null なら未登録</param>
        public sealed record Status(
            bool HasStripeCustomer,
            DateTimeOffset? PaymentMethodRegisteredAt,
            Estimate Estimate);

        /// <summary>
        /// 支払い登録状況と見込み額。<paramref name="refresh"/> が true で Customer があれば Stripe に既定の支払い方法を
        /// 問い合わせて <c>account.payment_method_registered_at</c> を同期する（Checkout から戻った直後、Webhook より
        /// 先にマイページが描かれるレースを埋める）。Account が無ければ null。
        /// </summary>
        Task<Status?> GetStatusAsync(string accountSubject, UsageMonth month, bool refresh, CancellationToken cancellationToken);

        /// <summary>
        /// 見込み額だけを計算する（Stripe には触らない）。月次起票（PR-2）もこれを通して同じ金額を請求する。
        /// Account が無ければ null。
        /// </summary>
        Task<Estimate?> GetEstimateAsync(string accountSubject, UsageMonth month, CancellationToken cancellationToken);

        /// <summary>
        /// カード登録用の Checkout Session を作り、その URL を返す。Stripe Customer が無ければ先に作る。
        /// Account が無ければ null。
        /// </summary>
        Task<string?> CreateCheckoutSessionAsync(string accountSubject, CancellationToken cancellationToken);

        /// <summary>
        /// Customer Portal の URL を返す。Customer 未作成なら <see cref="BillingException"/>（<c>no_customer</c>）。
        /// Account が無ければ null。
        /// </summary>
        Task<string?> CreatePortalSessionAsync(string accountSubject, CancellationToken cancellationToken);

        /// <summary>
        /// Stripe Webhook を反映する。初回のイベントなら処理して true、再送（既に処理済み）なら何もせず false。
        /// 対象 Customer が当該テナントの Account に紐づかないイベントは無視する（true）。
        /// </summary>
        Task<bool> HandleWebhookAsync(string tenantName, StripeWebhookEnvelope envelope, CancellationToken cancellationToken);
    }

    /// <summary>課金操作のクライアント起因エラー。<see cref="Error"/> はレスポンスの <c>error</c> にそのまま出す。</summary>
    public sealed class BillingException : Exception
    {
        public string Error { get; }
        public int StatusCode { get; }

        public BillingException(int statusCode, string error, string message) : base(message)
        {
            StatusCode = statusCode;
            Error = error;
        }
    }
}
