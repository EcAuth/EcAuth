using System.Data.Common;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace IdentityProvider.Test.TestHelpers
{
    /// <summary>
    /// 一過性として扱うテスト用例外。<see cref="RetryingTestExecutionStrategy"/> はこの例外だけを再試行する。
    /// </summary>
    public sealed class TransientTestException : Exception
    {
        public TransientTestException() : base("transient failure (test)") { }
    }

    /// <summary>
    /// <c>EnableRetryOnFailure</c> が生成する <c>SqlServerRetryingExecutionStrategy</c> の代替。
    /// 基底 <see cref="ExecutionStrategy"/> は <c>RetriesOnExceptions = true</c> なので、
    /// <c>CreateExecutionStrategy().ExecuteAsync</c> で包まれていない <c>BeginTransactionAsync</c> は
    /// 本番と同じ <see cref="InvalidOperationException"/>（user-initiated transactions 非対応）になる。
    /// 再試行間の待機は 0 にしてテストを速くする。
    /// </summary>
    public sealed class RetryingTestExecutionStrategy : ExecutionStrategy
    {
        public RetryingTestExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;
    }

    /// <summary>
    /// <see cref="Arm"/> 後の最初のトランザクションコミットが DB 上では成功した直後に、
    /// 一過性の失敗（<see cref="TransientTestException"/>）を報告するインターセプター。
    /// 「コミットは成功したが接続断でその応答が失われ、再試行戦略がデリゲートをやり直す」
    /// （EF Core docs の Transaction commit failure and the idempotency issue）を再現する。
    /// </summary>
    public sealed class FailAfterCommitOnceInterceptor : DbTransactionInterceptor
    {
        private bool _armed;

        /// <summary>武装後に失敗を報告したコミットの数（1 になれば発火済み）。</summary>
        public int FailedCommits { get; private set; }

        public void Arm() => _armed = true;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                FailedCommits++;
                throw new TransientTestException();
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 再試行戦略を載せた SQLite in-memory の <see cref="EcAuthDbContext"/>。
    ///
    /// <para>
    /// InMemory プロバイダーはトランザクションが no-op になり（<c>TransactionIgnoredWarning</c>）、
    /// 再試行戦略がユーザー開始トランザクションを拒否する挙動も再現できない。
    /// リレーショナルプロバイダーである SQLite に <see cref="RetryingTestExecutionStrategy"/> を載せることで、
    /// 「<c>BeginTransactionAsync</c> を使う経路が <c>DatabaseResilience.ExecuteInRetryableUnitAsync</c> で
    /// 包まれているか」と「再試行時に ChangeTracker が正しくリセットされるか」を実 DB 無しで検証する。
    /// </para>
    /// <para>
    /// SQLite の in-memory DB は接続を閉じた時点で消えるため、接続をこのオブジェクトの寿命に結びつける。
    /// </para>
    /// </summary>
    public sealed class RetryingSqliteContext : IDisposable
    {
        private readonly SqliteConnection _connection;

        public EcAuthDbContext Context { get; }

        public RetryingSqliteContext(ITenantService? tenantService = null, params IInterceptor[] interceptors)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<EcAuthDbContext>()
                .UseSqlite(_connection, sqlite => sqlite.ExecutionStrategy(d => new RetryingTestExecutionStrategy(d)))
                .AddInterceptors(interceptors)
                .Options;

            Context = new EcAuthDbContext(options, tenantService ?? new MockTenantService());
            Context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }
}
