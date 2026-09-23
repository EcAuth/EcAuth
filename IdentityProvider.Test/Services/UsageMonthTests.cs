using System.Globalization;
using IdentityProvider.Services;

namespace IdentityProvider.Test.Services
{
    public class UsageMonthTests
    {
        [Theory]
        // 月末 15:00Z 以降は JST では翌月（UTC で切ると前月に計上されてしまう 9 時間）
        [InlineData("2026-08-31T15:00:00Z", "2026-09")]
        [InlineData("2026-08-31T14:59:59Z", "2026-08")]
        [InlineData("2026-12-31T15:00:00Z", "2027-01")]
        [InlineData("2026-01-15T00:00:00Z", "2026-01")]
        // 入力のオフセットに依らず JST に正規化する
        [InlineData("2026-09-01T00:30:00+09:00", "2026-09")]
        [InlineData("2026-08-31T23:30:00-05:00", "2026-09")]
        public void FromInstant_UsesJstMonthBoundary(string instant, string expected)
        {
            var month = UsageMonth.FromInstant(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));

            Assert.Equal(expected, month.Value);
            Assert.Equal(expected, month.ToString());
        }

        [Fact]
        public void Value_IsInvariantUnderJapaneseCalendarCulture()
        {
            // 和暦カレンダーのカルチャで "yyyy" が "0008" になる事故を防ぐ
            var original = CultureInfo.CurrentCulture;
            try
            {
                var jaJp = new CultureInfo("ja-JP");
                jaJp.DateTimeFormat.Calendar = new JapaneseCalendar();
                CultureInfo.CurrentCulture = jaJp;

                var month = UsageMonth.FromInstant(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));

                Assert.Equal("2026-08", month.Value);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Theory]
        [InlineData("2026-01")]
        [InlineData("2026-12")]
        [InlineData("1999-06")]
        public void TryParse_AcceptsYyyyMm(string value)
        {
            Assert.True(UsageMonth.TryParse(value, out var month));
            Assert.Equal(value, month.Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("2026-13")]
        [InlineData("2026-00")]
        [InlineData("2026-1")]
        [InlineData("202608")]
        [InlineData("2026-08-01")]
        [InlineData(" 2026-08")]
        [InlineData("2026-08\n")]
        [InlineData("２０２６-08")] // 全角数字
        public void TryParse_RejectsOtherShapes(string? value)
        {
            Assert.False(UsageMonth.TryParse(value, out _));
        }

        [Theory]
        // 正規表現の [0-9]{4} は通るが Start を構築できない月。受理すると 422 のはずの入力が 500 になる。
        [InlineData("0000-01")] // 西暦 0 年そのものが表現できない
        [InlineData("0001-01")] // +09:00 を適用すると UTC が 0000-12-31T15:00Z になり範囲外
        public void TryParse_RejectsMonthsBelowMin(string value)
        {
            Assert.False(UsageMonth.TryParse(value, out _));
        }

        [Theory]
        [InlineData("0001-02")] // 下限そのもの
        [InlineData("9999-12")] // 正規表現が許す上限
        public void TryParse_AcceptsRepresentableBounds(string value)
        {
            Assert.True(UsageMonth.TryParse(value, out var month));

            Assert.Equal(value, month.Value);
            // 受理した月は必ず Start を構築できる（例外を投げない）
            Assert.Equal(1, month.Start.Day);
            Assert.Equal(UsageMonth.JstOffset, month.Start.Offset);
        }

        [Fact]
        public void Min_IsTheSmallestAcceptedMonth()
        {
            Assert.Equal("0001-02", UsageMonth.Min.Value);
            Assert.True(UsageMonth.TryParse("0001-02", out var min));
            Assert.Equal(UsageMonth.Min, min);
        }

        [Fact]
        public void Start_IsJstMonthStart()
        {
            Assert.True(UsageMonth.TryParse("2026-08", out var august));

            Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, UsageMonth.JstOffset), august.Start);
            // UTC では前月末 15:00Z
            Assert.Equal(new DateTimeOffset(2026, 7, 31, 15, 0, 0, TimeSpan.Zero), august.Start.ToUniversalTime());
        }

        [Fact]
        public void Comparison_OrdersByYearThenMonth()
        {
            Assert.True(UsageMonth.TryParse("2026-08", out var aug));
            Assert.True(UsageMonth.TryParse("2026-09", out var sep));
            Assert.True(UsageMonth.TryParse("2027-01", out var nextJan));

            Assert.True(aug < sep);
            Assert.True(sep < nextJan);
            Assert.True(nextJan > aug);
            Assert.True(aug <= aug);
            Assert.Equal(aug, UsageMonth.FromInstant(new DateTimeOffset(2026, 8, 1, 0, 0, 0, UsageMonth.JstOffset)));
        }
    }
}
