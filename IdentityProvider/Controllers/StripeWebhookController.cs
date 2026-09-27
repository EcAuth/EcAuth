using Asp.Versioning;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// Stripe Webhook の受け口（EcAuthDocs#119）。認証は <c>Stripe-Signature</c> の HMAC 検証のみ。
    /// <para>
    /// テナント（accounts = live / stg-accounts = test）は Host から <see cref="TenantMiddleware"/> が解決し、
    /// 署名シークレットもテナント別（<c>Stripe:WebhookSecret:{tenant}</c>）。Stripe ダッシュボードには
    /// テナントごとに 1 本ずつエンドポイントを登録する。
    /// </para>
    /// <para>
    /// 応答: 署名不正は 400（Stripe は再送しない）、処理成功・再送・無関係なイベントは 200、
    /// 処理中の例外は 500（Stripe が最大 3 日間再送する。処理は <see cref="StripeWebhookEvent"/> で冪等）。
    /// </para>
    /// </summary>
    [Route("v{version:apiVersion}/billing/stripe")]
    [ApiController]
    [ApiVersion("1.0")]
    public class StripeWebhookController : ControllerBase
    {
        /// <summary>Webhook ボディの上限。Stripe のイベントは数十 KB なので 1 MB あれば十分。</summary>
        private const long MaxPayloadBytes = 1024 * 1024;

        private readonly IStripeGateway _stripe;
        private readonly IBillingService _billing;
        private readonly ITenantService _tenantService;
        private readonly IOptions<BillingOptions> _options;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            IStripeGateway stripe,
            IBillingService billing,
            ITenantService tenantService,
            IOptions<BillingOptions> options,
            ILogger<StripeWebhookController> logger)
        {
            _stripe = stripe;
            _billing = billing;
            _tenantService = tenantService;
            _options = options;
            _logger = logger;
        }

        /// <summary>POST /v1/billing/stripe/webhook</summary>
        [HttpPost("webhook")]
        [RequestSizeLimit(MaxPayloadBytes)]
        public async Task<IActionResult> Receive()
        {
            if (!_options.Value.Enabled)
            {
                return NotFound(new { error = "not_found", error_description = "指定されたリソースが見つかりません。" });
            }

            string payload;
            using (var reader = new StreamReader(Request.Body))
            {
                payload = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            }

            var tenant = _tenantService.TenantName;
            StripeWebhookEnvelope envelope;
            try
            {
                envelope = _stripe.ParseWebhookEvent(tenant, payload, Request.Headers["Stripe-Signature"].FirstOrDefault());
            }
            catch (StripeWebhookSignatureException ex)
            {
                _logger.LogWarning(ex, "Stripe Webhook の署名検証に失敗しました: Tenant={Tenant}", tenant);
                return BadRequest(new { error = "invalid_signature", error_description = "署名を検証できません。" });
            }

            var processed = await _billing.HandleWebhookAsync(tenant, envelope, HttpContext.RequestAborted);
            _logger.LogInformation(
                "Stripe Webhook を受信しました: Tenant={Tenant}, Event={EventId}, Type={Type}, Processed={Processed}",
                tenant, envelope.Id, envelope.Type, processed);
            return Ok(new { received = true, processed });
        }
    }
}
