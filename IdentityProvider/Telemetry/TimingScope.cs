using System.Diagnostics;
using System.Globalization;

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
        private readonly Activity? _activity;

        private TimingScope(string stepName)
        {
            _stepName = stepName;
            // 開始時点の Activity.Current を捕捉する。AsyncLocal ベースの Activity.Current は
            // using ブロック内の await や子 Activity の Start/Stop で変化し得るため、
            // Dispose 時に取得すると意図と異なる Activity（または子 Activity）にタグが付く可能性がある。
            _activity = Activity.Current;
            _startTimestamp = _activity != null ? Stopwatch.GetTimestamp() : 0;
        }

        public static TimingScope Begin(string stepName) => new(stepName);

        public void Dispose()
        {
            if (_activity == null)
            {
                return;
            }

            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            // Azure Monitor の OpenTelemetry エクスポーター (Azure.Monitor.OpenTelemetry.AspNetCore)
            // は Activity タグの数値型 (double) を customDimensions にマッピングしないため、
            // 明示的に文字列化する。クエリ側は todouble(customDimensions["..."]) で数値として扱う。
            // フォーマットはロケール非依存にするため InvariantCulture を指定。
            _activity.SetTag(
                $"step.{_stepName}.elapsed_ms",
                elapsedMs.ToString("F3", CultureInfo.InvariantCulture));
        }
    }
}
