using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IdentityProvider.Models
{
    /// <summary>
    /// 月次請求ジョブの実行記録（対象月ごとに 1 行、EcAuthDocs#119）。
    /// <see cref="CompletedAt"/> が立っていれば「その月は全 Account の処理を終えた」ので、毎時の確認は
    /// Account を走査せずに済む。1 件でも失敗した実行は完了にせず、次の確認で未処理の Account だけやり直す
    /// （処理済みかどうかは <see cref="BillingInvoice"/> の行で判定する）。
    /// </summary>
    [Table("billing_invoice_run")]
    public class BillingInvoiceRun
    {
        /// <summary>対象月（<c>YYYY-MM</c>）。</summary>
        [Key]
        [Column("year_month")]
        [MaxLength(7)]
        public string YearMonth { get; set; } = string.Empty;

        [Column("started_at")]
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>全 Account の処理を終えた時刻。null は未完了（実行中、または失敗があった）。</summary>
        [Column("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }

        [Column("attempts")]
        public int Attempts { get; set; }

        /// <summary>直近の実行の失敗件数（0 なら完了）。</summary>
        [Column("failed")]
        public int Failed { get; set; }

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
