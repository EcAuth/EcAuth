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

        /// <summary>
        /// 受理する最小の月。<see cref="Start"/>（JST の月初）が <see cref="DateTimeOffset"/> として
        /// 表現できる下限で、<see cref="TryParse"/> がこれ未満を弾くことで <see cref="Start"/> は必ず構築できる。
        ///
        /// <para>
        /// <c>0000-01</c> は西暦 0 年そのものが表現できず、<c>0001-01</c> も +09:00 を適用すると UTC が
        /// <c>0000-12-31T15:00Z</c> になって範囲外になる。どちらも検証を通すと、不正な <c>year_month</c> が
        /// 422 ではなく 500 になる。
        /// </para>
        /// </summary>
        public static readonly UsageMonth Min = new(1, 2);

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
        /// この月の開始時刻（JST の月初 00:00:00）。
        /// 「対象月の時点で有効だったか」を判定するのに使う（<see cref="UsageReportService.IsBillable"/>）。
        /// <see cref="TryParse"/> が <see cref="Min"/> 未満を弾くため、常に構築できる。
        /// </summary>
        public DateTimeOffset Start => new(Year, Month, 1, 0, 0, 0, JstOffset);

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
        /// <c>"yyyy-MM"</c>（月は 01〜12、桁は ASCII 数字のみ）かつ <see cref="Min"/> 以降を受理する。それ以外は false。
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

            // 正規表現の [0-9]{4} は 0000 も通す。下限を弾かないと Start の構築で例外になり、
            // 不正な入力が 422 ではなく 500 になる（上限 9999-12 は Start を構築できるので追加の検証は不要）。
            var candidate = new UsageMonth(year, mon);
            if (candidate < Min)
            {
                return false;
            }

            month = candidate;
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
