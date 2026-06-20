using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

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
        // クリーンアップ実行間隔（日次）。
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        // トークンの保持期間。作成から 7 日を過ぎた行を削除する。
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MagicLinkCleanupService> _logger;

        public MagicLinkCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<MagicLinkCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 起動直後に 1 回実行し、以降は Interval ごとに実行する。
            using var timer = new PeriodicTimer(Interval);
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

            var cutoff = DateTimeOffset.UtcNow - Retention;
            var deleted = await context.MagicLoginTokens
                .Where(t => t.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "期限切れマジックリンクトークンを {Count} 件削除しました（保持期間: {RetentionDays} 日）。",
                    deleted, Retention.TotalDays);
            }
        }
    }
}
