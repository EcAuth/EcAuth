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
        public async Task<bool> EnsureDefaultPaymentMethodAsync(
            string tenantName, string customerId, CancellationToken cancellationToken)
        {
            var client = ClientFor(tenantName);
            var customer = await client.V1.Customers.GetAsync(customerId, cancellationToken: cancellationToken);
            if (customer.Deleted == true)
            {
                _logger.LogWarning("Stripe Customer が削除されています: Customer={CustomerId}", customerId);
                return false;
            }

            if (!string.IsNullOrEmpty(customer.InvoiceSettings?.DefaultPaymentMethodId))
            {
                return true;
            }

            // 既定が無い。Checkout の setup モードで付いたカードが残っていれば、最初の 1 枚を既定にする。
            var methods = await client.V1.Customers.PaymentMethods.ListAsync(
                customerId,
                new CustomerPaymentMethodListOptions { Type = "card", Limit = 1 },
                cancellationToken: cancellationToken);
            var first = methods.Data.FirstOrDefault();
            if (first == null)
            {
                return false;
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
            return true;
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

        /// <summary>
        /// 検証済みの <see cref="Event"/> から EcAuth が使う情報だけを取り出す。
        /// <c>payment_method.detached</c> はデタッチ後の PaymentMethod が載るため <c>data.object.customer</c> が null。
        /// その場合は <c>data.previous_attributes.customer</c>（変更前の値）から元の Customer を取る。
        /// </summary>
        public static StripeWebhookEnvelope ToEnvelope(Event stripeEvent)
        {
            string? customerId = null;
            string? checkoutMode = null;
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
                    break;
            }

            if (customerId == null
                && stripeEvent.Data.PreviousAttributes is Newtonsoft.Json.Linq.JObject previous
                && previous.TryGetValue("customer", out var previousCustomer)
                && previousCustomer.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                customerId = previousCustomer.ToString();
            }

            return new StripeWebhookEnvelope(stripeEvent.Id, stripeEvent.Type, customerId, checkoutMode);
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
