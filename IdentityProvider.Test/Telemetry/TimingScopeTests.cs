using IdentityProvider.Telemetry;
using System.Diagnostics;

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

            var tag = activity.GetTagItem("step.sample_step.elapsed_ms");
            Assert.NotNull(tag);
            Assert.IsType<double>(tag);
            Assert.True((double)tag! >= 0.0);
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
    }
}
