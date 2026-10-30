using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Stripe;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// <see cref="IStripeGateway"/> の本番実装（Stripe.net）。
    /// <para>
    /// API キーはテナント別の設定 <c>Stripe:SecretKey:{tenant}</c>（環境変数 <c>Stripe__SecretKey__accounts</c> 等、
    /// Key Vault 参照）から読む。キーのテナント部は <c>Signup:ConfirmBaseUrl:{tenant}</c> と同じく
    /// <c>[A-Za-z0-9_]</c> 以外を <c>_</c> に正規化する（<c>stg-accounts</c> → <c>stg_accounts</c>）。
    /// 未設定のテナントからの呼び出しは <see cref="InvalidOperationException"/>（fail-closed）。
    /// </para>
    /// <para>
    /// singleton 登録。<see cref="StripeClient"/> はスレッドセーフで、テナントごとに 1 つをキャッシュする。
    /// </para>
    /// </summary>
    public sealed class StripeGateway : IStripeGateway
    {
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        /// <summary>Webhook 署名のタイムスタンプ許容（秒）。Stripe.net の既定と同じ 5 分。</summary>
        private const long WebhookToleranceSeconds = 300;

        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeGateway> _logger;
        private readonly ConcurrentDictionary<string, StripeClient> _clients = new(StringComparer.Ordinal);

        public StripeGateway(IConfiguration configuration, ILogger<StripeGateway> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<string> CreateCustomerAsync(
            string tenantName, string email, string? name, IReadOnlyDictionary<string, string> metadata,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var customer = await client.V1.Customers.CreateAsync(
                new CustomerCreateOptions
                {
                    Email = email,
                    Name = name,
                    Metadata = new Dictionary<string, string>(metadata),
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            return customer.Id;
        }

        /// <inheritdoc />
        public async Task<string> CreateSetupCheckoutSessionAsync(
            string tenantName, string customerId, string successUrl, string cancelUrl, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var session = await client.V1.Checkout.Sessions.CreateAsync(
                new Stripe.Checkout.SessionCreateOptions
                {
                    Mode = "setup",
                    Customer = customerId,
                    // MVP はカードのみ（requirements.html §5.2）。
                    PaymentMethodTypes = new List<string> { "card" },
                    Currency = "jpy",
                    // 請求先住所を必須にして Customer に残す。国内は TaxRate で足りるが、海外の顧客へ Stripe Tax
                    //（automatic_tax）で売るときに課税判定に要る（EcAuthDocs#119）。
                    BillingAddressCollection = "required",
                    CustomerUpdate = new Stripe.Checkout.SessionCustomerUpdateOptions { Address = "auto" },
                    SuccessUrl = successUrl,
                    CancelUrl = cancelUrl,
                },
                cancellationToken: cancellationToken);

            return session.Url
                ?? throw new InvalidOperationException("Stripe Checkout Session に url がありません。");
        }

        /// <inheritdoc />
        public async Task<string> CreatePortalSessionAsync(
            string tenantName, string customerId, string returnUrl, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var session = await client.V1.BillingPortal.Sessions.CreateAsync(
                new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customerId,
                    ReturnUrl = returnUrl,
                },
                cancellationToken: cancellationToken);
            return session.Url;
        }

        /// <inheritdoc />
        public async Task<PaymentMethodState> EnsureDefaultPaymentMethodAsync(
            string tenantName, string customerId, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            Customer customer;
            try
            {
                customer = await client.V1.Customers.GetAsync(customerId, cancellationToken: cancellationToken);
            }
            catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
            {
                // 別モード（live / test）の ID や、既に消えた Customer。削除済みと同じ扱いで紐付けを外す。
                _logger.LogWarning("Stripe Customer が見つかりません: Customer={CustomerId}", customerId);
                return PaymentMethodState.CustomerDeleted;
            }
            if (customer.Deleted == true)
            {
                _logger.LogWarning("Stripe Customer が削除されています: Customer={CustomerId}", customerId);
                return PaymentMethodState.CustomerDeleted;
            }

            if (!string.IsNullOrEmpty(customer.InvoiceSettings?.DefaultPaymentMethodId))
            {
                return PaymentMethodState.Registered;
            }

            // 既定が無い。Checkout の setup モードで付いたカードが残っていれば、最初の 1 枚を既定にする。
            var methods = await client.V1.Customers.PaymentMethods.ListAsync(
                customerId,
                new CustomerPaymentMethodListOptions { Type = "card", Limit = 1 },
                cancellationToken: cancellationToken);
            var first = methods.Data.FirstOrDefault();
            if (first == null)
            {
                return PaymentMethodState.NotRegistered;
            }

            await client.V1.Customers.UpdateAsync(
                customerId,
                new CustomerUpdateOptions
                {
                    InvoiceSettings = new CustomerInvoiceSettingsOptions { DefaultPaymentMethod = first.Id },
                },
                cancellationToken: cancellationToken);
            _logger.LogInformation(
                "Stripe Customer の既定の支払い方法を設定しました: Customer={CustomerId}, PaymentMethod={PaymentMethodId}",
                customerId, first.Id);
            return PaymentMethodState.Registered;
        }

        /// <inheritdoc />
        public StripeWebhookEnvelope ParseWebhookEvent(string tenantName, string payload, string? signatureHeader)
        {
            var secret = _configuration[ConfigKey("WebhookSecret", tenantName)];
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"Stripe の Webhook 署名シークレットが未設定です（{ConfigKey("WebhookSecret", tenantName)}）。");
            }

            // Stripe.net はヘッダーが無いと StripeException ではなく NullReferenceException を投げる（500 になる）ので、
            // 署名不正と同じ扱いにして先に弾く。本物の Stripe の配信には必ず付く。
            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                throw new StripeWebhookSignatureException("Stripe-Signature ヘッダーがありません。");
            }

            Event stripeEvent;
            try
            {
                // アカウントの API バージョンと Stripe.net の想定バージョンのズレは警告に留める
                //（不一致で例外にすると Stripe 側のバージョン更新のたびに Webhook が全滅する）。
                stripeEvent = EventUtility.ConstructEvent(
                    payload, signatureHeader, secret, WebhookToleranceSeconds, throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                throw new StripeWebhookSignatureException("Stripe Webhook の署名検証に失敗しました。", ex);
            }

            return ToEnvelope(stripeEvent);
        }

        /// <inheritdoc />
        public async Task<StripeInvoiceSnapshot> CreateInvoiceAsync(
            string tenantName, InvoiceDraftRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var taxRateKey = ConfigKey("TaxRateId", tenantName);
            var taxRateId = _configuration[taxRateKey];
            if (string.IsNullOrWhiteSpace(taxRateId))
            {
                // 税率なしで起票すると消費税を取りこぼした請求書が確定してしまう。起票ごと止める。
                throw new InvalidOperationException($"Stripe の消費税の TaxRate が未設定です（{taxRateKey}）。");
            }

            await EnsureJapaneseLocaleAsync(client, request.CustomerId, cancellationToken);

            var invoice = await client.V1.Invoices.CreateAsync(
                new InvoiceCreateOptions
                {
                    Customer = request.CustomerId,
                    Currency = "jpy",
                    CollectionMethod = "charge_automatically",
                    // 明細を積み終えるまでは確定させない（ScheduleFinalizationAsync で後から予約する）。
                    AutoAdvance = false,
                    // Customer に溜まった他の InvoiceItem（ダッシュボードでの手作業など）をこの請求書に混ぜない。
                    PendingInvoiceItemsBehavior = "exclude",
                    DefaultTaxRates = new List<string> { taxRateId },
                    Description = request.Description,
                    Metadata = new Dictionary<string, string>(request.Metadata),
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            return ToSnapshot(invoice);
        }

        public async Task<string> AddInvoiceItemAsync(
            string tenantName, string customerId, string invoiceId, long amountJpy, string description,
            IReadOnlyDictionary<string, string> metadata, string idempotencyKey, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var item = await client.V1.InvoiceItems.CreateAsync(
                new InvoiceItemCreateOptions
                {
                    Customer = customerId,
                    Invoice = invoiceId,
                    Currency = "jpy",
                    // 税抜の金額。消費税は Invoice の default_tax_rates で付く（明細ごとに丸めない）。
                    Amount = amountJpy,
                    Description = description,
                    Metadata = new Dictionary<string, string>(metadata),
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            return item.Id;
        }

        /// <inheritdoc />
        public async Task<StripeInvoiceSnapshot> ScheduleFinalizationAsync(
            string tenantName, string invoiceId, DateTimeOffset finalizesAt, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var invoice = await client.V1.Invoices.UpdateAsync(
                invoiceId,
                new InvoiceUpdateOptions
                {
                    // この時刻に Stripe が確定して既定の支払い方法へ請求する。それまで（既定 2 日）は削除で止められる。
                    AutoAdvance = true,
                    AutomaticallyFinalizesAt = finalizesAt.UtcDateTime,
                },
                cancellationToken: cancellationToken);
            return ToSnapshot(invoice);
        }

        /// <inheritdoc />
        public async Task<string?> FindInvoiceIdByMetadataAsync(
            string tenantName, string customerId, string key, string value, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            await foreach (var invoice in client.V1.Invoices.ListAutoPagingAsync(
                new InvoiceListOptions { Customer = customerId, Limit = 100 }, cancellationToken: cancellationToken))
            {
                if (invoice.Metadata != null && invoice.Metadata.TryGetValue(key, out var v) && v == value)
                {
                    return invoice.Id;
                }
            }
            return null;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<StripeInvoiceItemSnapshot>> ListInvoiceItemsAsync(
            string tenantName, string invoiceId, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var items = new List<StripeInvoiceItemSnapshot>();
            await foreach (var item in client.V1.InvoiceItems.ListAutoPagingAsync(
                new InvoiceItemListOptions { Invoice = invoiceId, Limit = 100 }, cancellationToken: cancellationToken))
            {
                items.Add(new StripeInvoiceItemSnapshot(
                    item.Id, item.Amount, item.Metadata ?? new Dictionary<string, string>()));
            }
            return items;
        }

        /// <inheritdoc />
        public async Task<StripeInvoiceSnapshot?> GetInvoiceAsync(
            string tenantName, string invoiceId, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            try
            {
                var invoice = await client.V1.Invoices.GetAsync(invoiceId, cancellationToken: cancellationToken);
                return invoice.Deleted == true ? null : ToSnapshot(invoice);
            }
            catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
            {
                // 下書きを削除すると取得できなくなる（別モードの ID も同じ扱い）。
                return null;
            }
        }

        public async Task<StripeInvoiceSnapshot> CancelInvoiceAsync(
            string tenantName, string invoiceId, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var invoice = await client.V1.Invoices.GetAsync(invoiceId, cancellationToken: cancellationToken);
            switch (invoice.Status)
            {
                case "draft":
                    await client.V1.Invoices.DeleteAsync(invoiceId, cancellationToken: cancellationToken);
                    return ToSnapshot(invoice) with { Status = StripeInvoiceSnapshot.DeletedStatus };
                case "open":
                    return ToSnapshot(await client.V1.Invoices.VoidInvoiceAsync(invoiceId, cancellationToken: cancellationToken));
                default:
                    throw new InvalidOperationException(
                        $"Invoice {invoiceId} は status={invoice.Status} のため取り消せません（支払い済みの返金は Stripe のダッシュボードで行ってください）。");
            }
        }

        /// <summary>
        /// 請求書 PDF とメールの言語は Customer の <c>preferred_locales</c> で決まる（未設定だとアカウントの既定）。
        /// 未設定のときだけ日本語にし、顧客がダッシュボード経由で選んだ言語は上書きしない。
        /// </summary>
        private static async Task EnsureJapaneseLocaleAsync(StripeClient client, string customerId, CancellationToken cancellationToken)
        {
            var customer = await client.V1.Customers.GetAsync(customerId, cancellationToken: cancellationToken);
            if (customer.PreferredLocales is { Count: > 0 })
            {
                return;
            }
            await client.V1.Customers.UpdateAsync(
                customerId,
                new CustomerUpdateOptions { PreferredLocales = new List<string> { "ja" } },
                cancellationToken: cancellationToken);
        }

        private static StripeInvoiceSnapshot ToSnapshot(Invoice invoice)
        {
            DateTimeOffset? finalizesAt = invoice.AutomaticallyFinalizesAt is { } at
                ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
                : null;
            return new StripeInvoiceSnapshot(
                invoice.Id,
                invoice.Status,
                invoice.TotalTaxes?.Sum(t => t.Amount) ?? 0,
                invoice.Total,
                invoice.AmountPaid,
                finalizesAt);
        }

        /// <summary>
        /// 検証済みの <see cref="Event"/> から EcAuth が使う情報だけを取り出す。
        /// <c>payment_method.detached</c> はデタッチ後の PaymentMethod が載るため <c>data.object.customer</c> が null。
        /// その場合は <c>data.previous_attributes.customer</c>（変更前の値）から元の Customer を取る。
        /// </summary>
        public static StripeWebhookEnvelope ToEnvelope(Event stripeEvent)
        {
            string? customerId = null;
            string? checkoutMode = null;
            string? invoiceId = null;
            switch (stripeEvent.Data.Object)
            {
                case Stripe.Checkout.Session session:
                    customerId = session.CustomerId;
                    checkoutMode = session.Mode;
                    break;
                case SetupIntent setupIntent:
                    customerId = setupIntent.CustomerId;
                    break;
                case Customer customer:
                    customerId = customer.Id;
                    break;
                case PaymentMethod paymentMethod:
                    customerId = paymentMethod.CustomerId;
                    break;
                case Invoice invoice:
                    customerId = invoice.CustomerId;
                    invoiceId = invoice.Id;
                    break;
            }

            if (customerId == null
                && stripeEvent.Data.PreviousAttributes is Newtonsoft.Json.Linq.JObject previous
                && previous.TryGetValue("customer", out var previousCustomer)
                && previousCustomer.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                customerId = previousCustomer.ToString();
            }

            return new StripeWebhookEnvelope(stripeEvent.Id, stripeEvent.Type, customerId, checkoutMode, invoiceId);
        }

        private StripeClient ClientFor(string tenantName)
        {
            return _clients.GetOrAdd(tenantName, tenant =>
            {
                var key = ConfigKey("SecretKey", tenant);
                var apiKey = _configuration[key];
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException($"Stripe の API キーが未設定です（{key}）。");
                }
                return new StripeClient(apiKey);
            });
        }

        /// <summary><c>Stripe:{name}:{tenant}</c>。テナント部は env-var-safe に正規化。</summary>
        public static string ConfigKey(string name, string tenantName)
            => $"Stripe:{name}:{NonConfigKeyChar.Replace(tenantName, "_")}";
    }
}
