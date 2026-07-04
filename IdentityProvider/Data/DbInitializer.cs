using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data;

/// <summary>
/// データベース初期化処理を行うクラス。
/// 登録されたシーダーを順番に実行し、必要なシードデータを投入します。
/// </summary>
public class DbInitializer
{
    // DB 接続確立待ちのリトライ既定値。
    // デプロイ起動の一瞬だけ DB が未応答だと CanConnectAsync が false を返し、
    // 全シーダー（OrganizationClientSeeder の B2C→B2B 補正含む）がスキップされる事象への対策。
    // App Service の warmup プローブは起動完了まで約 70 秒待つため、その猶予内でリトライする。
    // 既定は最大 12 回・間隔 5 秒 = 最大約 55 秒待機。非秘密のチューニング定数のため
    // 配線（Terraform / CI / .env）には入れず、必要時のみ appsettings/env で上書きする。
    private const int DefaultMaxConnectionAttempts = 12;
    private const int DefaultConnectionRetryDelaySeconds = 5;

    private readonly IEnumerable<IDbSeeder> _seeders;
    private readonly ILogger<DbInitializer> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public DbInitializer(
        IEnumerable<IDbSeeder> seeders,
        ILogger<DbInitializer> logger,
        ILoggerFactory loggerFactory)
    {
        _seeders = seeders;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// データベースに接続可能かどうかを確認します。
    /// テスト時にオーバーライドして接続状態を制御できます。
    /// </summary>
    protected virtual async Task<bool> CanConnectAsync(EcAuthDbContext context)
        => await context.Database.CanConnectAsync();

    /// <summary>
    /// リトライ間の待機を行います。
    /// テスト時にオーバーライドして実時間の待機を回避できます。
    /// </summary>
    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);

    /// <summary>
    /// DB 接続が確立できるまでリトライ付きで待機します。
    /// デプロイ起動直後の一過性の DB 未応答でシーダーが丸ごとスキップされるのを防ぎます。
    /// </summary>
    /// <returns>接続が確立できた場合は true、全リトライ後も確立できなかった場合は false</returns>
    private async Task<bool> WaitForDatabaseAsync(EcAuthDbContext context, IConfiguration configuration)
    {
        var maxAttempts = configuration.GetValue(
            "DbInitializer:ConnectionRetry:MaxAttempts", DefaultMaxConnectionAttempts);
        var delaySeconds = configuration.GetValue(
            "DbInitializer:ConnectionRetry:DelaySeconds", DefaultConnectionRetryDelaySeconds);

        // 不正値（0 以下）でも最低 1 回は試行する
        if (maxAttempts < 1)
        {
            maxAttempts = 1;
        }
        if (delaySeconds < 0)
        {
            delaySeconds = 0;
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await CanConnectAsync(context))
            {
                return true;
            }

            if (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "DbInitializer: Database not ready (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}s",
                    attempt,
                    maxAttempts,
                    delaySeconds);
                await DelayAsync(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        return false;
    }

    /// <summary>
    /// 適用済みマイグレーションの一覧を取得します。
    /// テスト時にオーバーライドしてマイグレーション状態を制御できます。
    /// </summary>
    protected virtual async Task<HashSet<string>> GetAppliedMigrationsAsync(EcAuthDbContext context)
        => (await context.Database.GetAppliedMigrationsAsync()).ToHashSet();

    /// <summary>
    /// データベースの初期化処理を実行します。
    /// マイグレーションバージョンを確認し、適用済みのマイグレーションに対応するシードデータのみを投入します。
    /// </summary>
    /// <param name="context">データベースコンテキスト</param>
    /// <param name="configuration">アプリケーション設定</param>
    public async Task InitializeAsync(
        EcAuthDbContext context,
        IConfiguration configuration)
    {
        // データベース接続確認（一過性の未応答に耐えるためリトライ付きで待機）
        if (!await WaitForDatabaseAsync(context, configuration))
        {
            _logger.LogWarning("DbInitializer: Database is not available. Skipping seed.");
            return;
        }

        // 適用済みマイグレーションを取得
        var appliedMigrations = await GetAppliedMigrationsAsync(context);

        _logger.LogInformation("DbInitializer: Found {Count} applied migrations", appliedMigrations.Count);

        // シーダーを Order 順にソートして実行
        var orderedSeeders = _seeders.OrderBy(s => s.Order);

        foreach (var seeder in orderedSeeders)
        {
            var seederType = seeder.GetType();
            var seederName = seederType.Name;

            if (appliedMigrations.Contains(seeder.RequiredMigration))
            {
                _logger.LogInformation("DbInitializer: Running {Seeder}", seederName);

                // シーダー固有のロガーを作成
                var seederLogger = _loggerFactory.CreateLogger(seederType);

                try
                {
                    await seeder.SeedAsync(context, configuration, seederLogger);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DbInitializer: Error running {Seeder}", seederName);
                    throw;
                }
            }
            else
            {
                _logger.LogInformation(
                    "DbInitializer: Skipping {Seeder} - migration {Migration} not applied yet",
                    seederName,
                    seeder.RequiredMigration);
            }
        }

        _logger.LogInformation("DbInitializer: Initialization completed");
    }
}
