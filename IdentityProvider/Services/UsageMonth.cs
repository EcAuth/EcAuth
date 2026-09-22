using System.Globalization;
using System.Text.RegularExpressions;

namespace IdentityProvider.Services
{
    /// <summary>
    /// MAU 集計の対象月（<c>"yyyy-MM"</c>）。<c>monthly_active_user.year_month</c> の生成と検証をここに閉じ込める。
    ///
    /// <para>
    /// 月境界は <b>JST（+09:00 固定）</b>。UTC で切ると毎月末 15:00Z〜24:00Z の 9 時間が前月に計上され、
    /// 請求書の暦月と恒久的にズレる。<c>TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")</c> を使わないのは、
    /// JST は 1951 年以降 DST が無く固定オフセットで正しいうえ、tzdata の無いコンテナで throw する事故を避けるため。
    /// </para>
    /// <para>
    /// 書式化は <see cref="CultureInfo.InvariantCulture"/> 固定。スレッドのカルチャが和暦カレンダーだと
    /// <c>"yyyy"</c> が <c>"0008"</c> になるため、年月は整数で保持して桁指定で出力する。
    /// </para>
    /// </summary>
    public readonly partial record struct UsageMonth : IComparable<UsageMonth>
    {
        /// <summary>
        /// JST の固定オフセット。
        /// </summary>
        public static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);

        public int Year { get; }
        public int Month { get; }

        private UsageMonth(int year, int month)
        {
            Year = year;
            Month = month;
        }

        /// <summary>
        /// <c>"yyyy-MM"</c> 形式の値。DB の <c>year_month</c> にそのまま入れる。
        /// </summary>
        public string Value => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");

        /// <summary>
        /// 指定時刻が属する JST の月。
        /// </summary>
        public static UsageMonth FromInstant(DateTimeOffset instant)
        {
            var jst = instant.ToOffset(JstOffset);
            return new UsageMonth(jst.Year, jst.Month);
        }

        /// <summary>
        /// 現在時刻（JST）の月。
        /// </summary>
        public static UsageMonth Current() => FromInstant(DateTimeOffset.UtcNow);

        /// <summary>
        /// <c>"yyyy-MM"</c>（月は 01〜12、桁は ASCII 数字のみ）を受理する。それ以外は false。
        /// </summary>
        public static bool TryParse(string? value, out UsageMonth month)
        {
            month = default;
            if (value == null)
            {
                return false;
            }

            var m = YearMonthPattern().Match(value);
            if (!m.Success)
            {
                return false;
            }

            var year = int.Parse(m.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var mon = int.Parse(m.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            month = new UsageMonth(year, mon);
            return true;
        }

        public int CompareTo(UsageMonth other)
        {
            var byYear = Year.CompareTo(other.Year);
            return byYear != 0 ? byYear : Month.CompareTo(other.Month);
        }

        public static bool operator <(UsageMonth left, UsageMonth right) => left.CompareTo(right) < 0;
        public static bool operator >(UsageMonth left, UsageMonth right) => left.CompareTo(right) > 0;
        public static bool operator <=(UsageMonth left, UsageMonth right) => left.CompareTo(right) <= 0;
        public static bool operator >=(UsageMonth left, UsageMonth right) => left.CompareTo(right) >= 0;

        public override string ToString() => Value;

        // 末尾は $ ではなく \z（$ は末尾の改行の手前にも一致する）
        [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<m>0[1-9]|1[0-2])\z", RegexOptions.CultureInvariant)]
        private static partial Regex YearMonthPattern();
    }
}
