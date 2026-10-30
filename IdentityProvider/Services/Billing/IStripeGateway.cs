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
        /// 戻り値は Customer の現在の状態。Customer が Stripe 側で削除されていれば <see cref="PaymentMethodState.CustomerDeleted"/>。
        /// Webhook 受信時と、Checkout から戻ったマイページの再同期の両方で使う。
        /// </summary>
        Task<PaymentMethodState> EnsureDefaultPaymentMethodAsync(
            string tenantName,
            string customerId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Webhook の生ボディと <c>Stripe-Signature</c> を検証して、EcAuth が使う情報だけに畳んだ
        /// <see cref="StripeWebhookEnvelope"/> を返す。署名不正・改ざん・タイムスタンプ超過は
        /// <see cref="StripeWebhookSignatureException"/>。
        /// </summary>
        StripeWebhookEnvelope ParseWebhookEvent(string tenantName, string payload, string? signatureHeader);

        /// <summary>
        /// 月次請求の Invoice を下書き（draft）で作る（EcAuthDocs#119 PR-2）。
        /// <c>collection_method=charge_automatically</c>、<c>pending_invoice_items_behavior=exclude</c>（Customer に
        /// 溜まった他の InvoiceItem を拾わない）、消費税はテナント別の TaxRate（<c>Stripe:TaxRateId:{tenant}</c>）を
        /// <c>default_tax_rates</c> に付けて Stripe に計算させる。<paramref name="idempotencyKey"/> は Stripe の Idempotency-Key。
        /// <para>
        /// 作成時は自動確定しない（<c>auto_advance=false</c>）。明細を積み終える前に確定すると、欠けた明細（割引など）の
        /// まま請求され、確定後の Invoice には明細を足せないため直せない。確定の予約は全明細を積んだ後に
        /// <see cref="ScheduleFinalizationAsync"/> で入れる。
        /// </para>
        /// </summary>
        Task<StripeInvoiceSnapshot> CreateInvoiceAsync(
            string tenantName,
            InvoiceDraftRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken);

        /// <summary>
        /// 下書きの Invoice に明細を 1 行足し、InvoiceItem の ID（<c>ii_*</c>）を返す。金額は税抜の円（負なら割引）。
        /// </summary>
        Task<string> AddInvoiceItemAsync(
            string tenantName,
            string customerId,
            string invoiceId,
            long amountJpy,
            string description,
            IReadOnlyDictionary<string, string> metadata,
            string idempotencyKey,
            CancellationToken cancellationToken);

        /// <summary>
        /// 全明細を積み終えた下書きに自動確定を予約する（<c>auto_advance=true</c>、<c>automatically_finalizes_at</c>）。
        /// その時刻に Stripe が確定し、既定の支払い方法へ請求する。同じ値で何度呼んでもよい。
        /// </summary>
        Task<StripeInvoiceSnapshot> ScheduleFinalizationAsync(
            string tenantName,
            string invoiceId,
            DateTimeOffset finalizesAt,
            CancellationToken cancellationToken);

        /// <summary>Invoice の現在の状態。Stripe に無い（削除済みの下書きなど）なら null。</summary>
        Task<StripeInvoiceSnapshot?> GetInvoiceAsync(
            string tenantName,
            string invoiceId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Invoice を取り消す。確定前（draft）は削除（戻り値の <see cref="StripeInvoiceSnapshot.Status"/> は
        /// <see cref="StripeInvoiceSnapshot.DeletedStatus"/>）、確定後で未払い（open）は void にする。
        /// 支払い済み（paid）などは取り消せないので <see cref="InvalidOperationException"/>（返金は Stripe のダッシュボードで行う）。
        /// </summary>
        Task<StripeInvoiceSnapshot> CancelInvoiceAsync(
            string tenantName,
            string invoiceId,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// 月次 Invoice の作成内容（<see cref="IStripeGateway.CreateInvoiceAsync"/>）。明細は作成後に
    /// <see cref="IStripeGateway.AddInvoiceItemAsync"/> で足す。
    /// </summary>
    public sealed record InvoiceDraftRequest(
        string CustomerId,
        string Description,
        IReadOnlyDictionary<string, string> Metadata);

    /// <summary>
    /// Stripe の Invoice のうち EcAuth が写す部分。金額は円（JPY はゼロ小数通貨なので Stripe の値そのまま）。
    /// </summary>
    /// <param name="Status">Stripe の status（draft / open / paid / uncollectible / void）。削除済みの下書きは <see cref="DeletedStatus"/></param>
    /// <param name="TaxJpy">消費税額（Stripe が計算）。下書きの間は明細の追加で変わる</param>
    /// <param name="TotalJpy">合計（税込）</param>
    public sealed record StripeInvoiceSnapshot(
        string Id,
        string Status,
        long TaxJpy,
        long TotalJpy,
        long AmountPaidJpy,
        DateTimeOffset? AutomaticallyFinalizesAt)
    {
        public const string DeletedStatus = "deleted";
    }

    /// <summary><see cref="IStripeGateway.EnsureDefaultPaymentMethodAsync"/> が返す Customer の状態。</summary>
    public enum PaymentMethodState
    {
        /// <summary>既定の支払い方法がある</summary>
        Registered,
        /// <summary>Customer はあるが支払い方法が無い（Checkout キャンセル、カード削除など）</summary>
        NotRegistered,
        /// <summary>Customer が Stripe 側で削除されている（ダッシュボード / API での削除）。EcAuth 側の紐付けを外す</summary>
        CustomerDeleted,
    }

    /// <summary>
    /// Webhook イベントのうち EcAuth が見る部分。
    /// </summary>
    /// <param name="Id">イベント ID（<c>evt_*</c>）。冪等化のキー</param>
    /// <param name="Type">イベント種別</param>
    /// <param name="CustomerId">イベントの対象 Customer（<c>cus_*</c>）。Customer に紐づかないイベントは null</param>
    /// <param name="CheckoutMode"><c>checkout.session.completed</c> のときの mode（<c>setup</c> 等）。それ以外は null</param>
    /// <param name="InvoiceId"><c>invoice.*</c> のときの Invoice ID（<c>in_*</c>）。それ以外は null</param>
    public sealed record StripeWebhookEnvelope(
        string Id, string Type, string? CustomerId, string? CheckoutMode, string? InvoiceId = null);

    /// <summary>Webhook の署名検証に失敗した（→ 400）。</summary>
    public sealed class StripeWebhookSignatureException : Exception
    {
        public StripeWebhookSignatureException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
