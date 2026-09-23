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
        private readonly ConcurrentDictionary<string, bool> _customersHavePaymentMethod = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<string> CreateCustomerAsync(
            string tenantName, string email, string? name, IReadOnlyDictionary<string, string> metadata,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            // 同じ冪等キーなら同じ ID を返す（本物の Idempotency-Key と同じ性質）。
            var id = "cus_fake_" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey)))[..24];
            _customersHavePaymentMethod.TryAdd(id, false);
            return Task.FromResult(id);
        }

        /// <inheritdoc />
        public Task<string> CreateSetupCheckoutSessionAsync(
            string tenantName, string customerId, string successUrl, string cancelUrl, CancellationToken cancellationToken)
        {
            _customersHavePaymentMethod[customerId] = true;
            return Task.FromResult(successUrl);
        }

        /// <inheritdoc />
        public Task<string> CreatePortalSessionAsync(
            string tenantName, string customerId, string returnUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(returnUrl);
        }

        /// <inheritdoc />
        public Task<bool> EnsureDefaultPaymentMethodAsync(string tenantName, string customerId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_customersHavePaymentMethod.TryGetValue(customerId, out var has) && has);
        }

        /// <summary>テスト用: Customer の支払い方法の有無を直接変える（カードが外れた状況の再現）。</summary>
        public void SetPaymentMethod(string customerId, bool hasPaymentMethod)
        {
            _customersHavePaymentMethod[customerId] = hasPaymentMethod;
        }

        /// <inheritdoc />
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
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var obj))
                {
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
                return new StripeWebhookEnvelope(id, type, customerId, mode);
            }
            catch (JsonException ex)
            {
                throw new StripeWebhookSignatureException("Webhook ボディを JSON として読めません。", ex);
            }
        }
    }
}
