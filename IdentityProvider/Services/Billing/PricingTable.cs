using System.Text.Json;
using System.Text.Json.Serialization;
using IdentityProvider.Models;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// 料金の 1 帯。<see cref="UpTo"/> 以下の MAU に <see cref="UnitPriceJpy"/> を適用する（<c>null</c> は上限なし）。
    /// </summary>
    /// <param name="UpTo">この帯の上限 MAU（含む）。最後の帯は null</param>
    /// <param name="UnitPriceJpy">この帯の 1 MAU あたりの単価（円、税込）</param>
    public sealed record PriceTier(
        [property: JsonPropertyName("up_to")] int? UpTo,
        [property: JsonPropertyName("unit_price_jpy")] int UnitPriceJpy);

    /// <summary>
    /// 料金表（requirements.html §5.1、EcAuthDocs#119、2026-09-24 決定）。
    /// <para>
    /// どちらも段階従量（graduated）: 各帯の単価はその帯に属する MAU の分だけに適用する。
    /// 無料枠を超えた瞬間に一定額が発生する崖を作らず、規模が大きくなるほど限界単価が下がる。
    /// 表示価格・請求額は税込で、Stripe Tax は使わない。
    /// </para>
    /// <para>
    /// 単価はここにだけ書く。マイページの見込み額と請求額は必ず <see cref="IPricingCalculator"/> を通し、
    /// 帯の計算を他所に複製しない（<see cref="IUsageReportService"/> と同じ複製禁止ルール）。
    /// 変更するときは設計文書 §5.1 と Issue #119 本文も同時に更新する。
    /// Account 別の独自料金表（<see cref="AccountBillingPlan"/>）も同じ <see cref="PriceTier"/> の列で表し、
    /// <see cref="ParseTiers"/> / <see cref="Validate"/> で形を揃える。
    /// </para>
    /// </summary>
    public static class PricingTable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };

        /// <summary>B2B パスキー（管理画面）: Client ごとに 5 MAU まで無料、6 MAU 目以降 ¥100/MAU/月。</summary>
        public static readonly IReadOnlyList<PriceTier> B2B = new[]
        {
            new PriceTier(5, 0),
            new PriceTier(null, 100),
        };

        /// <summary>B2C フロント認証: 〜50 無料 / 51〜1,000 ¥20 / 1,001〜5,000 ¥15 / 5,001〜 ¥10。</summary>
        public static readonly IReadOnlyList<PriceTier> B2C = new[]
        {
            new PriceTier(50, 0),
            new PriceTier(1_000, 20),
            new PriceTier(5_000, 15),
            new PriceTier(null, 10),
        };

        /// <summary>
        /// 種別ごとの標準料金表。<see cref="SubjectType.Account"/>（マイページのコンソール Client）は課金対象外なので
        /// ここには無く、渡すと <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        public static IReadOnlyList<PriceTier> For(SubjectType subjectType) => subjectType switch
        {
            SubjectType.B2B => B2B,
            SubjectType.B2C => B2C,
            _ => throw new ArgumentOutOfRangeException(nameof(subjectType), subjectType, "課金対象外の SubjectType です。"),
        };

        /// <summary>標準料金表の無料枠。上限強制の閾値に使う。</summary>
        public static int FreeTierMau(SubjectType subjectType) => FreeTierMau(For(subjectType));

        /// <summary>無料枠（単価 0 の先頭帯の上限）。無料帯が無ければ 0、全帯が無料なら <see cref="int.MaxValue"/>。</summary>
        public static int FreeTierMau(IReadOnlyList<PriceTier> tiers)
        {
            var first = tiers[0];
            return first.UnitPriceJpy == 0 ? first.UpTo ?? int.MaxValue : 0;
        }

        /// <summary>
        /// 料金表の形を検証する。1 帯以上、<see cref="PriceTier.UpTo"/> は先頭から単調増加で最後の帯だけ null、
        /// 単価は 0 以上。違反は <see cref="ArgumentException"/>（独自料金表を保存・読込する側で捕まえる）。
        /// </summary>
        public static void Validate(IReadOnlyList<PriceTier> tiers)
        {
            if (tiers == null || tiers.Count == 0)
            {
                throw new ArgumentException("料金表には 1 帯以上が必要です。", nameof(tiers));
            }

            var previousUpTo = 0;
            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                if (tier.UnitPriceJpy < 0)
                {
                    throw new ArgumentException($"帯 {i + 1} の単価が負です: {tier.UnitPriceJpy}", nameof(tiers));
                }

                var isLast = i == tiers.Count - 1;
                if (isLast)
                {
                    if (tier.UpTo != null)
                    {
                        throw new ArgumentException("最後の帯の up_to は null（上限なし）にしてください。", nameof(tiers));
                    }
                    continue;
                }

                if (tier.UpTo is not int upTo)
                {
                    throw new ArgumentException($"帯 {i + 1} の up_to が null です。null は最後の帯だけに使えます。", nameof(tiers));
                }
                if (upTo <= previousUpTo)
                {
                    throw new ArgumentException($"帯 {i + 1} の up_to ({upTo}) は前の帯の上限 ({previousUpTo}) より大きくしてください。", nameof(tiers));
                }
                previousUpTo = upTo;
            }
        }

        /// <summary>
        /// 独自料金表の JSON（<c>[{"up_to":5,"unit_price_jpy":0},{"up_to":null,"unit_price_jpy":100}]</c>）を読む。
        /// 形式不正・<see cref="Validate"/> 違反は <see cref="ArgumentException"/>。
        /// </summary>
        public static IReadOnlyList<PriceTier> ParseTiers(string json)
        {
            List<PriceTier>? tiers;
            try
            {
                tiers = JsonSerializer.Deserialize<List<PriceTier>>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("料金表の JSON を読めません。", nameof(json), ex);
            }

            if (tiers == null)
            {
                throw new ArgumentException("料金表の JSON が null です。", nameof(json));
            }

            Validate(tiers);
            return tiers;
        }

        /// <summary><see cref="ParseTiers"/> の逆。保存前に <see cref="Validate"/> を通す。</summary>
        public static string SerializeTiers(IReadOnlyList<PriceTier> tiers)
        {
            Validate(tiers);
            return JsonSerializer.Serialize(tiers, JsonOptions);
        }
    }
}
