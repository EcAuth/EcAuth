namespace IdentityProvider.Services
{
    /// <summary>
    /// マジックリンクログイン（Phase D-2）の時間・回数ポリシーを集約した設定。
    /// <para>
    /// 既定値はコード内に持つため設定の配線（app_settings 等）は不要で、未設定でもそのまま動作する。
    /// 構成セクション <c>MagicLink</c> にキーがあれば上書きされる（例: 環境変数
    /// <c>MagicLink__RetentionDays</c>）。仕様が未確定で変更可能性が高いため、関連値を 1 か所に集約し
    /// 一望・変更しやすくする目的のクラス（環境別運用が必要になった場合は配線を追加するだけでよい）。
    /// </para>
    /// <para>
    /// トークンのバイト長（エントロピー）はセキュリティパラメータのため設定化せず、サービス側の
    /// 定数として固定する。
    /// </para>
    /// </summary>
    public sealed class MagicLinkOptions
    {
        /// <summary>構成セクション名（<c>MagicLink:BaseUrl:{tenant}</c> と同じ <c>MagicLink</c> 配下）。</summary>
        public const string SectionName = "MagicLink";

        /// <summary>マジックリンクトークンの有効期限（分）。設計上の既定は 10 分。</summary>
        public int TokenLifetimeMinutes { get; set; } = 10;

        /// <summary>同一メールアドレスのレート制限ウィンドウ（分）。このウィンドウ内は 1 回のみ許可。</summary>
        public int EmailRateLimitWindowMinutes { get; set; } = 5;

        /// <summary>同一 IP のレート制限ウィンドウ（分）。既定は 60 分。</summary>
        public int IpRateLimitWindowMinutes { get; set; } = 60;

        /// <summary>同一 IP がレート制限ウィンドウ内に発行できる最大回数。既定は 10 回。</summary>
        public int IpRateLimitMaxRequests { get; set; } = 10;

        /// <summary>期限切れトークン削除バッチの実行間隔（時間）。既定は 24 時間（日次）。</summary>
        public int CleanupIntervalHours { get; set; } = 24;

        /// <summary>トークンの保持期間（日）。作成からこの日数を過ぎた行を削除する。既定は 7 日。</summary>
        public int RetentionDays { get; set; } = 7;
    }
}
