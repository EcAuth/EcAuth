using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data;

/// <summary>
/// SQL 接続断への耐性に関する設定値と、ユーザー開始トランザクションを EF Core の
/// 再試行戦略（<c>EnableRetryOnFailure</c>）と両立させるためのヘルパー（EcAuth#536）。
/// </summary>
public static class DatabaseResilience
{
    /// <summary>
    /// リクエスト経路の単発 INSERT / SELECT に適用する <c>CommandTimeout</c>（秒）。
    /// 死んだ接続（旧コンテナ破棄時の SNAT 再利用等で応答が返らない）で 1 本のコマンドが
    /// ブロックされる時間を縛る。以前は 180 秒で、呼び出し元（ブラウザ / プラグイン）が
    /// とっくに諦めた後もサーバーだけが待ち続けていた。
    /// </summary>
    public const int RequestCommandTimeoutSeconds = 30;

    /// <summary>
    /// 起動時マイグレーション（<c>MigrateAsync</c>）に適用する <c>CommandTimeout</c>（秒）。
    /// backfill を含むマイグレーションは <see cref="RequestCommandTimeoutSeconds"/> では足りないため、
    /// <c>MigrateAsync</c> の前後だけ <c>SetCommandTimeout</c> で個別に延ばす。
    /// staging / production のマイグレーションは <c>dotnet ef migrations script</c> の SQL を
    /// <c>azure/sql-action</c> で適用するためこの値には依存しない（compose の
    /// <c>RUN_MIGRATIONS_ON_STARTUP</c> 経路のみ）。
    /// </summary>
    public const int MigrationCommandTimeoutSeconds = 180;

    /// <summary>
    /// 一過性の接続断に対する最大再試行回数。EF Core の既定（6 回 / 最大 30 秒）は
    /// リクエスト経路には長すぎるため縮める（最悪でも 10 秒程度で諦める）。
    /// </summary>
    public const int MaxRetryCount = 3;

    /// <summary>再試行間の最大待機時間。</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="DbUpdateException"/> が SQL Server のユニーク／主キー制約違反
    /// （エラー番号 2601 / 2627）に起因するかを判定する。
    ///
    /// <para>
    /// <see cref="ExecuteInRetryableUnitAsync"/> の中で <c>DbUpdateException</c> を捕まえて 409 等に
    /// 変換するときは、必ずこの判定で絞ること。一過性の接続断も <c>SaveChangesAsync</c> では
    /// <c>DbUpdateException</c> に包まれて届くため、無条件に捕まえるとデリゲートが正常終了扱いになり、
    /// 再試行戦略が働かない（<c>ExecutionStrategy</c> は <c>DbUpdateException</c> の InnerException を
    /// 見て再試行可否を判定するので、外へ逃がせば再試行される）。
    /// </para>
    /// </summary>
    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx
            && IsUniqueConstraintViolation(sqlEx);
    }

    /// <summary>
    /// <see cref="Microsoft.Data.SqlClient.SqlException"/> がユニーク／主キー制約違反（2601 / 2627）かを判定する。
    /// <c>ExecuteSqlInterpolatedAsync</c> 等の生 SQL は <see cref="DbUpdateException"/> に包まれず
    /// <c>SqlException</c> がそのまま飛ぶため、その経路ではこちらを使う。
    /// </summary>
    public static bool IsUniqueConstraintViolation(Microsoft.Data.SqlClient.SqlException ex)
    {
        return ex.Number == 2601 || ex.Number == 2627;
    }

    /// <summary>
    /// ユーザー開始トランザクション（<c>BeginTransactionAsync</c>）を含む一連の DB 操作を、
    /// 再試行戦略の 1 単位として実行する。
    ///
    /// <para>
    /// <c>EnableRetryOnFailure</c> を有効にした <c>SqlServerRetryingExecutionStrategy</c> は
    /// ユーザー開始トランザクションを許容せず、<c>CreateExecutionStrategy().ExecuteAsync</c> で
    /// 包まれていない <c>BeginTransactionAsync</c> は <see cref="InvalidOperationException"/> になる。
    /// 一過性の失敗時は <paramref name="operation"/> が丸ごと再実行されるため、
    /// 中で行う操作は再実行しても安全でなければならない（INSERT はユニーク制約で二重化を弾く）。
    /// </para>
    /// <para>
    /// 各試行の先頭で <c>ChangeTracker.Clear()</c> を呼ぶ。前回の試行が残した追跡状態
    /// （<c>Added</c> のままのエンティティや、<c>SaveChangesAsync</c> 済みだがロールバックされて
    /// <c>Unchanged</c> になっているエンティティ）を持ち越すと、再実行時に二重追跡や
    /// 存在しない行への FK 参照になるため。初回の試行でも同じく空にするので、
    /// <paramref name="operation"/> の外で読んだエンティティを中で更新する場合は
    /// <c>Attach</c> し直すこと（<c>SignupService.ConfirmAsync</c> の <c>signupRequest</c> が該当）。
    /// 呼び出し元が未保存のエンティティを追跡させたまま呼ぶと破棄されるので、
    /// リクエスト処理の主単位（コントローラー / サービスの入口）でのみ使う。
    /// </para>
    /// <para>
    /// コミット中に接続が切れると、コミットが成功していても一過性エラーとして再試行される
    /// （EF Core docs「Transaction commit failure and the idempotency issue」）。やり直しがユニーク制約で
    /// 409 になるだけなら許容できるが、応答と共に失われる値（申込確認の登録トークン等）がある経路は
    /// <see cref="ExecuteInRetryableUnitAsync{TResult}(EcAuthDbContext, Func{bool, CancellationToken, Task{TResult}}, CancellationToken)"/>
    /// の <c>isRetry</c> で「前回の試行がコミット済みか」を確認し、成功として返すこと。
    /// </para>
    /// </summary>
    public static Task<TResult> ExecuteInRetryableUnitAsync<TResult>(
        this EcAuthDbContext context,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        => context.ExecuteInRetryableUnitAsync((_, ct) => operation(ct), cancellationToken);

    /// <summary>
    /// <see cref="ExecuteInRetryableUnitAsync{TResult}(EcAuthDbContext, Func{CancellationToken, Task{TResult}}, CancellationToken)"/>
    /// と同じだが、デリゲートに「再試行か（2 回目以降の試行か）」を渡す。
    /// 再試行時に前回の試行のコミット結果を認識し、やり直しではなく成功として返すための判定に使う。
    /// </summary>
    public static Task<TResult> ExecuteInRetryableUnitAsync<TResult>(
        this EcAuthDbContext context,
        Func<bool, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return strategy.ExecuteAsync(
            async ct =>
            {
                attempt++;
                context.ChangeTracker.Clear();
                return await operation(attempt > 1, ct);
            },
            cancellationToken);
    }
}
