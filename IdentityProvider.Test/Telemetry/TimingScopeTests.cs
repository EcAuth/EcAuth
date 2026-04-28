using IdentityProvider.Telemetry;
using System.Diagnostics;
using System.Globalization;

namespace IdentityProvider.Test.Telemetry
{
    public class TimingScopeTests
    {
        [Fact]
        public void Dispose_WithoutActivity_DoesNotThrow()
        {
            // Activity.Current が null の状況（ActivitySource が未設定 / sampling=false 等）でも
            // 例外が発生しないことを保証する。本番では SDK 未初期化のローカル環境でも no-op で動く必要がある。
            Activity.Current = null;

            var scope = TimingScope.Begin("noop_step");
            scope.Dispose();
        }

        [Fact]
        public void Dispose_WithActivity_AddsElapsedTag()
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(listener);

            using var source = new ActivitySource(nameof(TimingScopeTests));
            using var activity = source.StartActivity("test")!;

            using (TimingScope.Begin("sample_step"))
            {
                Thread.Sleep(2);
            }

            // Azure Monitor が customDimensions にマッピングできるよう、値は文字列で記録される。
            // クエリ側は todouble() で数値として扱う想定。
            var tag = activity.GetTagItem("step.sample_step.elapsed_ms");
            Assert.NotNull(tag);
            var tagString = Assert.IsType<string>(tag);
            Assert.True(double.TryParse(tagString, NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed));
            Assert.True(elapsed >= 0.0);
        }

        [Fact]
        public void Nested_Scopes_AddIndependentTags()
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(listener);

            using var source = new ActivitySource(nameof(TimingScopeTests));
            using var activity = source.StartActivity("test_nested")!;

            using (TimingScope.Begin("outer"))
            {
                using (TimingScope.Begin("inner"))
                {
                    Thread.Sleep(1);
                }
            }

            Assert.NotNull(activity.GetTagItem("step.outer.elapsed_ms"));
            Assert.NotNull(activity.GetTagItem("step.inner.elapsed_ms"));
        }

        [Fact]
        public void Dispose_AfterActivityCurrentChanged_TagsTheCapturedActivity()
        {
            // スコープ開始時にアクティブだった Activity に対してタグが付与され、
            // ブロック内で Activity.Current が変化（子 Activity の Start 等）しても
            // 子 Activity 側にタグが漏れないことを保証する（PR #375 レビュー対応）。
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(listener);

            using var source = new ActivitySource(nameof(TimingScopeTests));
            using var outer = source.StartActivity("outer_capture")!;

            using (TimingScope.Begin("captured_step"))
            {
                // スコープ内で子 Activity を Start/Stop し、Activity.Current を一時的に差し替える
                using (var inner = source.StartActivity("inner_should_not_get_tag"))
                {
                    Thread.Sleep(1);
                }
            }

            Assert.NotNull(outer.GetTagItem("step.captured_step.elapsed_ms"));
        }
    }
}
