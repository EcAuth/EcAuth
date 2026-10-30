namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// 月次請求ジョブ（EcAuthDocs#119 PR-2）。時刻指定の cron ではなく、一定間隔で起きて
    /// 「当月 1 日の JST 03:00 を過ぎていて、前月分の実行が未完了」なら起票する
    /// （判定は <see cref="IMonthlyInvoicingService.RunIfDueAsync"/>）。再起動やデプロイで 03:00 を逃しても
    /// 次の確認で追いつき、失敗した Account も次の確認で再開する。
    /// <para>
    /// 確定とカードへの請求は起票時の <c>automatically_finalizes_at</c> に Stripe が行うので、このサービスは
    /// 起票だけを担う。複数インスタンスで同時に動いても、<c>billing_invoice</c> の UNIQUE と Stripe の冪等キーで
    /// 二重起票しない。<c>Billing:InvoicingEnabled</c> が false の間は何もしない（毎回の確認はフラグを見るだけ）。
    /// </para>
    /// <para>
    /// <see cref="BackgroundService"/> は singleton のため、scoped な依存（DbContext・テナント）は確認ごとに
    /// スコープを作って解決する（<see cref="MagicLinkCleanupService"/> と同じ形）。
    /// </para>
    /// </summary>
    public sealed class MonthlyInvoicingHostedService : BackgroundService
    {
        /// <summary>確認の間隔。起票は月 1 回なので、遅れても 1 時間以内に追いつけば足りる。</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _time;
        private readonly ILogger<MonthlyInvoicingHostedService> _logger;

        public MonthlyInvoicingHostedService(
            IServiceScopeFactory scopeFactory,
            TimeProvider time,
            ILogger<MonthlyInvoicingHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _time = time;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 起動直後に 1 回確認し、以降は一定間隔で確認する。
            using var timer = new PeriodicTimer(CheckInterval, _time);
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 失敗してもサービスは止めない（次の確認で再試行）。設定漏れ（InvoicingStartMonth 等）もここに出る。
                    _logger.LogError(ex, "月次請求ジョブの確認に失敗しました。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task CheckAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var invoicing = scope.ServiceProvider.GetRequiredService<IMonthlyInvoicingService>();
            var result = await invoicing.RunIfDueAsync(_time.GetUtcNow(), cancellationToken);
            if (result is { Failed: > 0 })
            {
                _logger.LogWarning(
                    "月次請求に失敗した Account があります。次の確認で再開します: Month={Month}, Failed={Failed}",
                    result.Month, result.Failed);
            }
        }
    }
}
