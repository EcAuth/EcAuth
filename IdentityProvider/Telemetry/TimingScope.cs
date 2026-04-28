using System.Diagnostics;

namespace IdentityProvider.Telemetry
{
    /// <summary>
    /// 各処理ステップの所要時間を Activity.Current のタグとして記録するスコープヘルパー。
    /// using ブロックの開始時に Stopwatch を起動し、Dispose 時に経過時間を
    /// step.{name}.elapsed_ms タグとして付与する。
    /// Application Insights / Azure Monitor は Activity タグを customDimensions に
    /// 自動マッピングするため、本番テレメトリ上でステップ単位の所要時間を
    /// クエリ可能になる（EcAuth/EcAuthDocs#69 のボトルネック特定用）。
    /// Activity.Current が null の場合（ローカル開発で Application Insights 未設定など）は
    /// no-op として動作する。
    /// </summary>
    public readonly struct TimingScope : IDisposable
    {
        private readonly string _stepName;
        private readonly long _startTimestamp;

        private TimingScope(string stepName)
        {
            _stepName = stepName;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public static TimingScope Begin(string stepName) => new(stepName);

        public void Dispose()
        {
            var activity = Activity.Current;
            if (activity == null)
            {
                return;
            }

            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            activity.SetTag($"step.{_stepName}.elapsed_ms", elapsedMs);
        }
    }
}
