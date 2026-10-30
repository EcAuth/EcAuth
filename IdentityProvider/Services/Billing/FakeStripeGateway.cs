using System.Collections.Concurrent;
using System.Text.Json;

namespace IdentityProvider.Services.Billing
{
    /// <summary>
    /// <see cref="IStripeGateway"/> のローカル / CI E2E 用実装（<c>Billing:Provider=Fake</c>）。Stripe に接続せず、
    /// Customer と支払い方法の有無をメモリに持つ。<c>Email:Provider=Smtp</c>（mailpit）と同じ位置づけの切替で、
    /// Production 環境では起動時に拒否される（Program.cs）。
    /// <para>
    /// 振る舞い: Checkout Session を作った時点でその Customer に支払い方法が付いたものとし、
    /// URL は <c>successUrl</c> をそのまま返す（ブラウザは Stripe を経由せずマイページに戻る）。
    /// Webhook は署名を検証せず JSON をそのまま読む（E2E が <c>checkout.session.completed</c> を直接 POST できる）。
    /// </para>
    /// </summary>
    public sealed class FakeStripeGateway : IStripeGateway
    {
        private readonly ConcurrentDictionary<string, PaymentMethodState> _customers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FakeInvoice> _invoices = new(StringComparer.Ordinal);
        // 冪等キー → 作成済みの ID（本物の Idempotency-Key と同じく、同じキーの再呼び出しは同じものを返す）
        private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
        private readonly object _invoiceLock = new();

        /// <summary>Fake の Invoice。テストと E2E が中身を確かめるために公開する。</summary>
        public sealed class FakeInvoice
        {
            public required string Id { get; init; }
            public required string TenantName { get; init; }
            public required string CustomerId { get; init; }
            public required DateTimeOffset FinalizesAt { get; init; }
            public required string Description { get; init; }
            public required IReadOnlyDictionary<string, string> Metadata { get; init; }
            public string Status { get; set; } = "draft";
            public long AmountPaid { get; set; }
            public List<(string Id, long AmountJpy, string Description)> Items { get; } = new();

            /// <summary>税抜合計。</summary>
            public long Subtotal => Items.Sum(i => i.AmountJpy);

            /// <summary>消費税（10%、請求書単位で 1 回だけ四捨五入。本物の請求書レベルの端数処理と同じ）。</summary>
            public long Tax => (long)Math.Round(Subtotal * 0.1m, MidpointRounding.AwayFromZero);
        }

        public IReadOnlyCollection<FakeInvoice> Invoices => _invoices.Values.ToList();

        /// <inheritdoc />
        public Task<string> CreateCustomerAsync(
            string tenantName, string email, string? name, IReadOnlyDictionary<string, string> metadata,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            // 同じ冪等キーなら同じ ID を返す（本物の Idempotency-Key と同じ性質）。
            var id = "cus_fake_" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey)))[..24];
            _customers.TryAdd(id, PaymentMethodState.NotRegistered);
            return Task.FromResult(id);
        }

        /// <inheritdoc />
        public Task<string> CreateSetupCheckoutSessionAsync(
            string tenantName, string customerId, string successUrl, string cancelUrl, CancellationToken cancellationToken)
        {
            _customers[customerId] = PaymentMethodState.Registered;
            return Task.FromResult(successUrl);
        }

        /// <inheritdoc />
        public Task<string> CreatePortalSessionAsync(
            string tenantName, string customerId, string returnUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(returnUrl);
        }

        /// <inheritdoc />
        public Task<PaymentMethodState> EnsureDefaultPaymentMethodAsync(string tenantName, string customerId, CancellationToken cancellationToken)
        {
            // 知らない ID は本物の resource_missing と同じく削除済み扱い
            return Task.FromResult(_customers.TryGetValue(customerId, out var state) ? state : PaymentMethodState.CustomerDeleted);
        }

        /// <summary>テスト用: Customer の支払い方法の有無を直接変える（カードが外れた状況の再現）。</summary>
        public void SetPaymentMethod(string customerId, bool hasPaymentMethod)
        {
            _customers[customerId] = hasPaymentMethod ? PaymentMethodState.Registered : PaymentMethodState.NotRegistered;
        }

        /// <summary>テスト用: Customer を Stripe 側で削除した状況を再現する。</summary>
        public void DeleteCustomer(string customerId)
        {
            _customers[customerId] = PaymentMethodState.CustomerDeleted;
        }

        /// <inheritdoc />
        public Task<StripeInvoiceSnapshot> CreateInvoiceAsync(
            string tenantName, InvoiceDraftRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            lock (_invoiceLock)
            {
                if (_idempotency.TryGetValue(idempotencyKey, out var existingId))
                {
                    return Task.FromResult(ToSnapshot(_invoices[existingId]));
                }
                var invoice = new FakeInvoice
                {
                    Id = "in_fake_" + Guid.NewGuid().ToString("N")[..24],
                    TenantName = tenantName,
                    CustomerId = request.CustomerId,
                    FinalizesAt = request.FinalizesAt,
                    Description = request.Description,
                    Metadata = new Dictionary<string, string>(request.Metadata),
                };
                _invoices[invoice.Id] = invoice;
                _idempotency[idempotencyKey] = invoice.Id;
                return Task.FromResult(ToSnapshot(invoice));
            }
        }

        public Task<string> AddInvoiceItemAsync(
            string tenantName, string customerId, string invoiceId, long amountJpy, string description,
            IReadOnlyDictionary<string, string> metadata, string idempotencyKey, CancellationToken cancellationToken)
        {
            lock (_invoiceLock)
            {
                if (_idempotency.TryGetValue(idempotencyKey, out var existingId))
                {
                    return Task.FromResult(existingId);
                }
                if (!_invoices.TryGetValue(invoiceId, out var invoice) || invoice.Status != "draft")
                {
                    throw new InvalidOperationException($"下書きの Invoice がありません: {invoiceId}");
                }
                var id = "ii_fake_" + Guid.NewGuid().ToString("N")[..24];
                invoice.Items.Add((id, amountJpy, description));
                _idempotency[idempotencyKey] = id;
                return Task.FromResult(id);
            }
        }

        public Task<StripeInvoiceSnapshot?> GetInvoiceAsync(string tenantName, string invoiceId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_invoices.TryGetValue(invoiceId, out var invoice) && invoice.Status != StripeInvoiceSnapshot.DeletedStatus
                ? ToSnapshot(invoice)
                : null);
        }

        public Task<StripeInvoiceSnapshot> CancelInvoiceAsync(string tenantName, string invoiceId, CancellationToken cancellationToken)
        {
            lock (_invoiceLock)
            {
                if (!_invoices.TryGetValue(invoiceId, out var invoice))
                {
                    throw new InvalidOperationException($"Invoice がありません: {invoiceId}");
                }
                invoice.Status = invoice.Status switch
                {
                    "draft" => StripeInvoiceSnapshot.DeletedStatus,
                    "open" => "void",
                    _ => throw new InvalidOperationException($"Invoice {invoiceId} は status={invoice.Status} のため取り消せません。"),
                };
                return Task.FromResult(ToSnapshot(invoice));
            }
        }

        /// <summary>Stripe 側の状態変化（確定・支払い）を再現する。</summary>
        public void SetInvoiceStatus(string invoiceId, string status)
        {
            var invoice = _invoices[invoiceId];
            invoice.Status = status;
            invoice.AmountPaid = status == "paid" ? invoice.Subtotal + invoice.Tax : 0;
        }

        private static StripeInvoiceSnapshot ToSnapshot(FakeInvoice invoice) => new(
            invoice.Id, invoice.Status, invoice.Tax, invoice.Subtotal + invoice.Tax, invoice.AmountPaid, invoice.FinalizesAt);

        public StripeWebhookEnvelope ParseWebhookEvent(string tenantName, string payload, string? signatureHeader)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                // ルートが object でない（JSON 文字列や配列）と GetProperty が InvalidOperationException になるので先に弾く。
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new StripeWebhookSignatureException("Webhook ボディが JSON object ではありません。");
                }
                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    throw new StripeWebhookSignatureException("Webhook ボディに id / type がありません。");
                }
                var id = idElement.GetString()!;
                var type = typeElement.GetString()!;

                string? customerId = null;
                string? mode = null;
                string? invoiceId = null;
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var obj))
                {
                    if (obj.TryGetProperty("object", out var objectKind) && objectKind.ValueKind == JsonValueKind.String
                        && objectKind.GetString() == "invoice"
                        && obj.TryGetProperty("id", out var iid) && iid.ValueKind == JsonValueKind.String)
                    {
                        invoiceId = iid.GetString();
                    }
                    if (obj.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        customerId = c.GetString();
                    }
                    else if (obj.TryGetProperty("object", out var kind) && kind.GetString() == "customer"
                             && obj.TryGetProperty("id", out var cid))
                    {
                        customerId = cid.GetString();
                    }
                    if (obj.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        mode = m.GetString();
                    }
                }
                return new StripeWebhookEnvelope(id, type, customerId, mode, invoiceId);
            }
            catch (JsonException ex)
            {
                throw new StripeWebhookSignatureException("Webhook ボディを JSON として読めません。", ex);
            }
        }
    }
}
