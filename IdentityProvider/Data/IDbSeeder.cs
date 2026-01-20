using IdentityProvider.Models;

namespace IdentityProvider.Data;

/// <summary>
/// データベースシーダーのインターフェース。
/// 各シーダーは必要なマイグレーションとシード処理を定義します。
/// </summary>
public interface IDbSeeder
{
    /// <summary>
    /// このシーダーが必要とするマイグレーション名。
    /// このマイグレーションが適用されている場合にのみシード処理が実行されます。
    /// </summary>
    string RequiredMigration { get; }

    /// <summary>
    /// シーダーの実行順序。小さい値が先に実行されます。
    /// </summary>
    int Order { get; }

    /// <summary>
    /// シード処理を実行します。
    /// </summary>
    /// <param name="context">データベースコンテキスト</param>
    /// <param name="configuration">アプリケーション設定</param>
    /// <param name="logger">ロガー</param>
    /// <returns>非同期タスク</returns>
    Task SeedAsync(EcAuthDbContext context, IConfiguration configuration, ILogger logger);
}
