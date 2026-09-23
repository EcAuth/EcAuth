using Asp.Versioning;
using IdentityProvider.Filters;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// マイページ向けの課金 API（EcAuthDocs#119）。<see cref="AccountController"/> と同じく Account トークン必須、
    /// accounts / stg-accounts テナントでのみ機能する。<c>Billing:Enabled</c> が false なら全て 404。
    /// CORS は既存の SignupApiCors（GET / POST / OPTIONS のみ）を流用するため、更新系も POST。
    /// </summary>
    [Route("v{version:apiVersion}/account/billing")]
    [ApiController]
    [ApiVersion("1.0")]
    [EnableCors(SignupController.CorsPolicy)]
    [NoStore]
    public class BillingController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IBillingService _billing;
        private readonly IOptions<BillingOptions> _options;
        private readonly ILogger<BillingController> _logger;

        public BillingController(
            ITokenService tokenService,
            IBillingService billing,
            IOptions<BillingOptions> options,
            ILogger<BillingController> logger)
        {
            _tokenService = tokenService;
            _billing = billing;
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// GET /v1/account/billing?year_month=yyyy-MM&amp;refresh=1
        /// 支払い方法の登録状況と、対象月（既定は当月）の見込み額（Organization → Client の内訳、請求対象外理由、割引）。
        /// <c>refresh=1</c> は Checkout から戻った直後に Stripe と同期する（Webhook より先に描画されるレース対策）。
        /// </summary>
        [HttpGet("")]
        public async Task<IActionResult> GetStatus([FromQuery] string? year_month = null, [FromQuery] string? refresh = null)
        {
            if (!_options.Value.Enabled)
            {
                return NotFoundResult();
            }

            var subject = await AccountTokenAuthentication.ValidateAccountTokenAsync(Request, _tokenService);
            if (subject == null)
            {
                return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
            }

            var current = UsageMonth.Current();
            UsageMonth month;
            if (year_month == null)
            {
                month = current;
            }
            else if (!UsageMonth.TryParse(year_month, out var parsed))
            {
                return InvalidInput("year_month は yyyy-MM 形式で指定してください。", "year_month");
            }
            else if (parsed > current)
            {
                return InvalidInput("year_month に未来の月は指定できません。", "year_month");
            }
            else
            {
                month = parsed;
            }

            var doRefresh = refresh == "1" || string.Equals(refresh, "true", StringComparison.OrdinalIgnoreCase);
            var status = await _billing.GetStatusAsync(subject, month, doRefresh, HttpContext.RequestAborted);
            if (status == null)
            {
                return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
            }

            return Ok(new
            {
                payment_method_registered = status.PaymentMethodRegisteredAt != null,
                payment_method_registered_at = status.PaymentMethodRegisteredAt,
                has_stripe_customer = status.HasStripeCustomer,
                estimate = new
                {
                    year_month = status.Estimate.Month.Value,
                    as_of = status.Estimate.AsOf,
                    subtotal_jpy = status.Estimate.SubtotalJpy,
                    discount_jpy = status.Estimate.DiscountJpy,
                    total_jpy = status.Estimate.TotalJpy,
                    plan = new
                    {
                        exempt = status.Estimate.Plan.Exempt,
                        discount_percent = status.Estimate.Plan.DiscountPercent,
                        discount_jpy = status.Estimate.Plan.DiscountJpy,
                        custom_b2b_pricing = status.Estimate.Plan.CustomB2BPricing,
                        custom_b2c_pricing = status.Estimate.Plan.CustomB2CPricing
                    },
                    organizations = status.Estimate.Organizations.Select(o => new
                    {
                        organization_id = o.OrganizationId,
                        code = o.Code,
                        name = o.Name,
                        is_sandbox = o.IsSandbox,
                        is_billable = o.IsBillable,
                        amount_jpy = o.AmountJpy,
                        clients = o.Clients.Select(c => new
                        {
                            id = c.Id,
                            client_id = c.ClientId,
                            app_name = c.AppName,
                            subject_type = c.SubjectType.ToString().ToLowerInvariant(),
                            monthly_active_users = c.MonthlyActiveUsers,
                            free_tier_mau = c.FreeTierMau,
                            billable_units = c.BillableUnits,
                            list_price_jpy = c.ListPriceJpy,
                            amount_jpy = c.AmountJpy,
                            is_billable = c.IsBillable,
                            exempt_reason = c.ExemptReason,
                            custom_pricing = c.CustomPricing
                        }).ToArray()
                    }).ToArray()
                }
            });
        }

        /// <summary>
        /// POST /v1/account/billing/checkout
        /// カード登録用の Stripe Checkout（setup モード）を作り、遷移先 URL を返す。
        /// </summary>
        [HttpPost("checkout")]
        public async Task<IActionResult> CreateCheckoutSession()
        {
            if (!_options.Value.Enabled)
            {
                return NotFoundResult();
            }

            var subject = await AccountTokenAuthentication.ValidateAccountTokenAsync(Request, _tokenService);
            if (subject == null)
            {
                return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
            }

            var url = await _billing.CreateCheckoutSessionAsync(subject, HttpContext.RequestAborted);
            if (url == null)
            {
                return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
            }

            return Ok(new { url });
        }

        /// <summary>
        /// POST /v1/account/billing/portal
        /// Stripe Customer Portal（カード変更・請求書閲覧）の URL を返す。支払い方法未登録なら 409 <c>no_customer</c>。
        /// </summary>
        [HttpPost("portal")]
        public async Task<IActionResult> CreatePortalSession()
        {
            if (!_options.Value.Enabled)
            {
                return NotFoundResult();
            }

            var subject = await AccountTokenAuthentication.ValidateAccountTokenAsync(Request, _tokenService);
            if (subject == null)
            {
                return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
            }

            try
            {
                var url = await _billing.CreatePortalSessionAsync(subject, HttpContext.RequestAborted);
                if (url == null)
                {
                    return Unauthorized(AccountTokenAuthentication.InvalidTokenBody());
                }
                return Ok(new { url });
            }
            catch (BillingException ex)
            {
                return StatusCode(ex.StatusCode, new { error = ex.Error, error_description = ex.Message });
            }
        }

        private IActionResult NotFoundResult()
            => NotFound(new { error = "not_found", error_description = "指定されたリソースが見つかりません。" });

        private IActionResult InvalidInput(string description, string field)
            => UnprocessableEntity(new { error = "invalid_request", error_description = description, field });
    }
}
