using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data;

/// <summary>
/// データベース初期化処理を行うクラス。
/// 登録されたシーダーを順番に実行し、必要なシードデータを投入します。
/// </summary>
public class DbInitializer
{
    private readonly IEnumerable<IDbSeeder> _seeders;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        IEnumerable<IDbSeeder> seeders,
        ILogger<DbInitializer> logger)
    {
        _seeders = seeders;
        _logger = logger;
    }

    /// <summary>
    /// データベースに接続可能かどうかを確認します。
    /// テスト時にオーバーライドして接続状態を制御できます。
    /// </summary>
    protected virtual async Task<bool> CanConnectAsync(EcAuthDbContext context)
        => await context.Database.CanConnectAsync();

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
        // データベース接続確認
        if (!await CanConnectAsync(context))
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
            var seederName = seeder.GetType().Name;

            if (appliedMigrations.Contains(seeder.RequiredMigration))
            {
                _logger.LogInformation("DbInitializer: Running {Seeder}", seederName);

                try
                {
                    await seeder.SeedAsync(context, configuration, _logger);
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
