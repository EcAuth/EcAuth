namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// Account と対象月から適用する <see cref="PricingPlan"/> を解決する（<see cref="Models.AccountBillingPlan"/> の読み出し）。
    /// 見込み額（<see cref="IBillingService"/>）と月次起票が同じ解決を通る。
    /// </summary>
    public interface IPricingPlanResolver
    {
        /// <summary>
        /// 対象月の開始時点（JST 1 日 0:00）で有効な設定があればそれを、無ければ <see cref="PricingPlan.Default"/>。
        /// 保存されている独自料金表が壊れていれば <see cref="InvalidOperationException"/>（黙って標準で請求しない）。
        /// </summary>
        Task<PricingPlan> ResolveAsync(string accountSubject, UsageMonth month, CancellationToken cancellationToken = default);
    }
}
