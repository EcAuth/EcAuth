using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 期限切れ・使用済みのマジックリンクトークンを日次で削除するバックグラウンドサービス。
    /// 保持期間（7 日）を過ぎた <see cref="MagicLoginToken"/> を <c>ExecuteDelete</c> で一括削除する。
    /// <para>
    /// <see cref="BackgroundService"/> は singleton のため、scoped な <see cref="EcAuthDbContext"/> は
    /// <see cref="IServiceScopeFactory"/> でリクエストスコープを作って解決する。
    /// </para>
    /// </summary>
    public class MagicLinkCleanupService : BackgroundService
    {
        // クリーンアップ実行間隔と保持期間（既定は日次・7 日）。MagicLinkOptions から導出する。
        private readonly TimeSpan _interval;
        private readonly TimeSpan _retention;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MagicLinkCleanupService> _logger;

        public MagicLinkCleanupService(
            IServiceScopeFactory scopeFactory,
            IOptions<MagicLinkOptions> options,
            ILogger<MagicLinkCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _interval = TimeSpan.FromHours(options.Value.CleanupIntervalHours);
            _retention = TimeSpan.FromDays(options.Value.RetentionDays);
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 起動直後に 1 回実行し、以降は設定された間隔ごとに実行する。
            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    await CleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // シャットダウンに伴うキャンセルは正常終了として扱う。
                    break;
                }
                catch (Exception ex)
                {
                    // クリーンアップの失敗はサービス継続を妨げない（次回実行で再試行）。
                    _logger.LogError(ex, "マジックリンクトークンのクリーンアップに失敗しました。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task CleanupAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<EcAuthDbContext>();

            var cutoff = DateTimeOffset.UtcNow - _retention;
            var deleted = await context.MagicLoginTokens
                .Where(t => t.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "期限切れマジックリンクトークンを {Count} 件削除しました（保持期間: {RetentionDays} 日）。",
                    deleted, _retention.TotalDays);
            }
        }
    }
}
