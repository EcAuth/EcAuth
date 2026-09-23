namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// 課金機能（EcAuthDocs#119）の有効化フラグと運用パラメータ。
    /// <para>
    /// 既定値はコード内に持つため配線（app_settings 等）は不要で、未設定なら課金機能は丸ごと無効
    /// （エンドポイントは 404、Webhook も受け付けない）。有効化するときだけ環境変数
    /// （例: <c>Billing__Enabled=true</c>）で上書きする。段階投入のため、機能ごとにフラグを分けている。
    /// </para>
    /// <para>
    /// 単価と無料枠は <see cref="PricingTable"/> の定数で持つ（ここには置かない）。料金表は
    /// 設計文書（requirements.html §5.1）と一致させる対象であり、環境ごとに変えるものではないため。
    /// </para>
    /// <para>
    /// Stripe の API キーと Webhook 署名シークレットは秘密のためここではなく
    /// <c>Stripe:SecretKey:{tenant}</c> / <c>Stripe:WebhookSecret:{tenant}</c>（Key Vault 参照）から
    /// <see cref="StripeGateway"/> が読む。Checkout / Portal からの戻り先 URL はテナント別の非秘密設定
    /// <c>Billing:ReturnBaseUrl:{tenant}</c> で、<c>MagicLink:BaseUrl:{tenant}</c> と同じ規約。
    /// </para>
    /// </summary>
    public sealed class BillingOptions
    {
        public const string SectionName = "Billing";

        /// <summary>Stripe 連携の実装。<c>Stripe</c>（既定）または <c>Fake</c>（ローカル / CI E2E 用）。</summary>
        public const string StripeProvider = "Stripe";
        public const string FakeProvider = "Fake";

        /// <summary>
        /// 課金 API（マイページの支払い登録・見込み額）と Stripe Webhook を有効にする。
        /// false なら <c>/v1/account/billing/*</c> と <c>/v1/billing/stripe/webhook</c> は 404。
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary><see cref="StripeProvider"/> / <see cref="FakeProvider"/>。Production で Fake は起動時に拒否する。</summary>
        public string Provider { get; set; } = StripeProvider;

        /// <summary>
        /// 無料枠超過 + 支払い方法未登録の Client で新規パスキー登録をブロックする（402）。
        /// 顧客への告知が済むまで false のままにする。
        /// </summary>
        public bool EnforcementEnabled { get; set; }

        /// <summary>月次の Invoice 起票ジョブを有効にする。</summary>
        public bool InvoicingEnabled { get; set; }

        /// <summary>
        /// Invoice を起票してから自動確定（＝カードへの請求）までの日数。既定 2 日。
        /// 誤請求を void で止める猶予。安定後は 0 で即時確定にできる。
        /// </summary>
        public int FinalizeAfterDays { get; set; } = 2;
    }
}
