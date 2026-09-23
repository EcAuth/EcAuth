namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// Stripe API の薄い抽象（EcAuthDocs#119）。EcAuth が使う操作だけを Stripe の型を出さずに切り出し、
    /// 本番実装（<see cref="StripeGateway"/>）とローカル / CI 用の <see cref="FakeStripeGateway"/> を差し替える。
    /// ユニットテストはこのインターフェースをモックする（リポジトリに HTTP モックの前例が無く、
    /// Stripe.net の型を組み立てるテストは壊れやすいため）。
    /// <para>
    /// 全メソッドは <paramref name="tenantName"/> を受け取る。accounts テナントは live、stg-accounts テナントは
    /// test のキーを使うため、呼び出し元（リクエストのテナント）ごとに Stripe クライアントが異なる。
    /// </para>
    /// </summary>
    public interface IStripeGateway
    {
        /// <summary>
        /// Stripe Customer を作成し、その ID（<c>cus_*</c>）を返す。
        /// <paramref name="idempotencyKey"/> を Stripe の Idempotency-Key に渡すので、同じキーでの再呼び出し
        /// （並行リクエストや DB 保存失敗後のやり直し）は同じ Customer を返す。
        /// </summary>
        Task<string> CreateCustomerAsync(
            string tenantName,
            string email,
            string? name,
            IReadOnlyDictionary<string, string> metadata,
            string idempotencyKey,
            CancellationToken cancellationToken);

        /// <summary>
        /// カード登録用の Checkout Session（<c>mode=setup</c>）を作り、顧客をリダイレクトする URL を返す。
        /// </summary>
        Task<string> CreateSetupCheckoutSessionAsync(
            string tenantName,
            string customerId,
            string successUrl,
            string cancelUrl,
            CancellationToken cancellationToken);

        /// <summary>
        /// Customer Portal の Session を作り、その URL を返す（カード変更・請求書の閲覧）。
        /// </summary>
        Task<string> CreatePortalSessionAsync(
            string tenantName,
            string customerId,
            string returnUrl,
            CancellationToken cancellationToken);

        /// <summary>
        /// Customer に既定の支払い方法があるようにする。既定が未設定でカードが 1 枚以上あれば
        /// 最初のカードを既定にする（Checkout の setup モードはカードを Customer に付けるだけで既定にはしない）。
        /// 戻り値は「既定の支払い方法がある」か。Webhook 受信時と、Checkout から戻ったマイページの再同期の両方で使う。
        /// </summary>
        Task<bool> EnsureDefaultPaymentMethodAsync(
            string tenantName,
            string customerId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Webhook の生ボディと <c>Stripe-Signature</c> を検証して、EcAuth が使う情報だけに畳んだ
        /// <see cref="StripeWebhookEnvelope"/> を返す。署名不正・改ざん・タイムスタンプ超過は
        /// <see cref="StripeWebhookSignatureException"/>。
        /// </summary>
        StripeWebhookEnvelope ParseWebhookEvent(string tenantName, string payload, string? signatureHeader);
    }

    /// <summary>
    /// Webhook イベントのうち EcAuth が見る部分。
    /// </summary>
    /// <param name="Id">イベント ID（<c>evt_*</c>）。冪等化のキー</param>
    /// <param name="Type">イベント種別</param>
    /// <param name="CustomerId">イベントの対象 Customer（<c>cus_*</c>）。Customer に紐づかないイベントは null</param>
    /// <param name="CheckoutMode"><c>checkout.session.completed</c> のときの mode（<c>setup</c> 等）。それ以外は null</param>
    public sealed record StripeWebhookEnvelope(string Id, string Type, string? CustomerId, string? CheckoutMode);

    /// <summary>Webhook の署名検証に失敗した（→ 400）。</summary>
    public sealed class StripeWebhookSignatureException : Exception
    {
        public StripeWebhookSignatureException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
