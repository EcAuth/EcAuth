using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    /// <summary>
    /// <see cref="BillingService"/>: 見込み額の組み立て（請求対象外理由・割引・独自帯）、Customer の遅延作成、
    /// Webhook の冪等化とテナント突合。Stripe は <see cref="FakeStripeGateway"/>。
    /// </summary>
    public class BillingServiceTests : IDisposable
    {
        private const string Tenant = "accounts";
        private const string Subject = "acct-subject-1";

        private readonly EcAuthDbContext _context;
        private readonly MockTenantService _tenantService = new();
        private readonly Mock<IAccountService> _accounts = new();
        private readonly Mock<IUsageReportService> _usage = new();
        private readonly FakeStripeGateway _stripe = new();
        private readonly BillingService _service;
        private readonly Account _account;
        private readonly UsageMonth _month = Month("2026-09");

        public BillingServiceTests()
        {
            _tenantService.SetTenant(Tenant);
            _context = TestDbContextHelper.CreateInMemoryContext(tenantService: _tenantService);

            var accountsOrg = new Organization { Id = 1, Code = "accounts", Name = "EcAuth", TenantName = Tenant };
            _account = new Account { Id = 1, Subject = Subject, Email = "owner@example.jp", OrganizationId = 1, Organization = accountsOrg };
            _context.Organizations.Add(accountsOrg);
            _context.Accounts.Add(_account);
            _context.SaveChanges();

            _accounts.Setup(a => a.GetBySubjectAsync(Subject)).ReturnsAsync(() => _account);
            _accounts.Setup(a => a.GetBySubjectAsync(It.IsNotIn(Subject))).ReturnsAsync((Account?)null);
            _accounts.Setup(a => a.GetManagedOrganizationsAsync(Subject))
                .ReturnsAsync(new List<IAccountService.ManagedOrganization> { new(10, "shop", "owner") });

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Billing:ReturnBaseUrl:accounts"] = "https://ec-auth.io/",
                })
                .Build();

            _service = new BillingService(
                _context,
                _tenantService,
                _accounts.Object,
                _usage.Object,
                new PricingPlanResolver(_context),
                new PricingCalculator(),
                _stripe,
                configuration,
                new Mock<ILogger<BillingService>>().Object);
        }

        private static UsageMonth Month(string value)
        {
            Assert.True(UsageMonth.TryParse(value, out var month));
            return month;
        }

        private IUsageReportService.ClientUsage Client(
            int id, SubjectType type, int mau, DateTimeOffset? createdAt = null, bool exempt = false) =>
            new(id, $"client-{id}", $"App {id}", type, mau, 0,
                createdAt ?? _month.Start.AddMonths(-3), exempt, exempt ? "検証用" : null);

        private void SetupReport(bool orgBillable, params IUsageReportService.ClientUsage[] clients)
        {
            var report = new IUsageReportService.UsageReport(_month, DateTimeOffset.UtcNow, new[]
            {
                new IUsageReportService.OrganizationUsage(10, "shop", "Shop", false, orgBillable, clients.Sum(c => c.MonthlyActiveUsers), clients),
            });
            _usage.Setup(u => u.GetReportAsync(
                    It.Is<IUsageReportService.UsageReportQuery>(q => q.Month == _month && q.IncludeNonBillable && q.OrganizationIds.Contains(10)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(report);
        }

        // ---- 見込み額 ----

        [Fact]
        public async Task GetStatus_StandardPlan_SumsClientsAndReportsNoCustomer()
        {
            SetupReport(orgBillable: true, Client(1, SubjectType.B2B, 10), Client(2, SubjectType.B2C, 100));

            var status = await _service.GetStatusAsync(Subject, _month, refresh: false, CancellationToken.None);

            Assert.NotNull(status);
            Assert.False(status.HasStripeCustomer);
            Assert.Null(status.PaymentMethodRegisteredAt);
            var est = status.Estimate;
            Assert.Equal(1_500, est.SubtotalJpy);   // B2B 10 → ¥500, B2C 100 → ¥1,000
            Assert.Equal(0, est.DiscountJpy);
            Assert.Equal(1_500, est.TotalJpy);
            Assert.False(est.Plan.Exempt);
            var org = Assert.Single(est.Organizations);
            Assert.Equal(1_500, org.AmountJpy);
            Assert.All(org.Clients, c => { Assert.True(c.IsBillable); Assert.Null(c.ExemptReason); Assert.Equal(c.ListPriceJpy, c.AmountJpy); });
        }

        [Fact]
        public async Task GetStatus_UnknownAccount_ReturnsNull()
        {
            Assert.Null(await _service.GetStatusAsync("nobody", _month, false, CancellationToken.None));
        }

        [Fact]
        public async Task Estimate_FirstMonth_ClientCreatedInTargetMonthIsFree()
        {
            SetupReport(orgBillable: true,
                Client(1, SubjectType.B2B, 10, createdAt: _month.Start.AddDays(20)),          // 当月作成 → 無料
                Client(2, SubjectType.B2B, 10, createdAt: _month.Start.AddMonths(1).AddHours(1)), // 翌月作成 → 対象月の初月ではない
                Client(3, SubjectType.B2B, 10, createdAt: _month.Start.AddSeconds(-1)));      // 前月末作成 → 通常課金

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            Assert.NotNull(est);
            var clients = est.Organizations.Single().Clients;
            Assert.Equal(IBillingService.ExemptReasons.FirstMonth, clients[0].ExemptReason);
            Assert.Equal(500, clients[0].ListPriceJpy);
            Assert.Equal(0, clients[0].AmountJpy);
            Assert.Null(clients[1].ExemptReason);
            Assert.Null(clients[2].ExemptReason);
            Assert.Equal(1_000, est.TotalJpy);
        }

        [Fact]
        public async Task Estimate_ClientExempt_IsZeroWithReason()
        {
            SetupReport(orgBillable: true, Client(1, SubjectType.B2C, 100, exempt: true), Client(2, SubjectType.B2C, 100));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            var clients = est!.Organizations.Single().Clients;
            Assert.Equal(IBillingService.ExemptReasons.ClientExempt, clients[0].ExemptReason);
            Assert.False(clients[0].IsBillable);
            Assert.Equal(1_000, clients[0].ListPriceJpy);
            Assert.Equal(1_000, est.TotalJpy);
        }

        [Fact]
        public async Task Estimate_OrganizationNotBillable_TakesPrecedenceOverClientReasons()
        {
            SetupReport(orgBillable: false, Client(1, SubjectType.B2C, 100, createdAt: _month.Start.AddDays(1), exempt: true));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            var client = est!.Organizations.Single().Clients.Single();
            Assert.Equal(IBillingService.ExemptReasons.OrganizationNotBillable, client.ExemptReason);
            Assert.Equal(0, est.TotalJpy);
        }

        [Fact]
        public async Task Estimate_AccountExempt_EverythingIsZeroWithAccountReason()
        {
            _context.AccountBillingPlans.Add(new AccountBillingPlan { AccountSubject = Subject, BillingExempt = true, ExemptReason = "社内" });
            await _context.SaveChangesAsync();
            SetupReport(orgBillable: true, Client(1, SubjectType.B2C, 1_000), Client(2, SubjectType.B2B, 10, exempt: true));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            Assert.True(est!.Plan.Exempt);
            Assert.All(est.Organizations.Single().Clients, c => Assert.Equal(IBillingService.ExemptReasons.AccountExempt, c.ExemptReason));
            Assert.Equal(19_000, est.Organizations.Single().Clients[0].ListPriceJpy);
            Assert.Equal(0, est.SubtotalJpy);
            Assert.Equal(0, est.TotalJpy);
        }

        [Fact]
        public async Task Estimate_DiscountPercent_AppliedOnceToAccountSubtotal()
        {
            _context.AccountBillingPlans.Add(new AccountBillingPlan { AccountSubject = Subject, DiscountPercent = 10 });
            await _context.SaveChangesAsync();
            SetupReport(orgBillable: true, Client(1, SubjectType.B2C, 100), Client(2, SubjectType.B2C, 500), Client(3, SubjectType.B2C, 60, exempt: true));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            Assert.Equal(10_000, est!.SubtotalJpy);   // 1,000 + 9,000（除外 Client は含まない）
            Assert.Equal(1_000, est.DiscountJpy);
            Assert.Equal(9_000, est.TotalJpy);
            Assert.Equal(10, est.Plan.DiscountPercent);
        }

        [Fact]
        public async Task Estimate_FixedDiscountLargerThanSubtotal_TotalIsZero()
        {
            _context.AccountBillingPlans.Add(new AccountBillingPlan { AccountSubject = Subject, DiscountJpy = 5_000 });
            await _context.SaveChangesAsync();
            SetupReport(orgBillable: true, Client(1, SubjectType.B2C, 100));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            Assert.Equal(1_000, est!.SubtotalJpy);
            Assert.Equal(1_000, est.DiscountJpy);
            Assert.Equal(0, est.TotalJpy);
        }

        [Fact]
        public async Task Estimate_CustomTiers_UsedForThatSubjectType()
        {
            _context.AccountBillingPlans.Add(new AccountBillingPlan
            {
                AccountSubject = Subject,
                B2BTiersJson = """[{"up_to":50,"unit_price_jpy":0},{"up_to":null,"unit_price_jpy":80}]""",
            });
            await _context.SaveChangesAsync();
            SetupReport(orgBillable: true, Client(1, SubjectType.B2B, 60), Client(2, SubjectType.B2C, 100));

            var est = await _service.GetEstimateAsync(Subject, _month, CancellationToken.None);

            var clients = est!.Organizations.Single().Clients;
            Assert.True(clients[0].CustomPricing);
            Assert.Equal(50, clients[0].FreeTierMau);
            Assert.Equal(800, clients[0].AmountJpy);
            Assert.False(clients[1].CustomPricing);
            Assert.Equal(1_000, clients[1].AmountJpy);
            Assert.True(est.Plan.CustomB2BPricing);
            Assert.False(est.Plan.CustomB2CPricing);
        }

        // ---- Customer / Checkout / Portal ----

        [Fact]
        public async Task CreateCheckout_CreatesCustomerOnceAndReturnsSuccessUrl()
        {
            var url1 = await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);
            var customerId = _account.StripeCustomerId;
            var url2 = await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);

            Assert.Equal("https://ec-auth.io/mypage/?billing=setup_complete", url1);
            Assert.Equal(url1, url2);
            Assert.StartsWith("cus_fake_", customerId);
            Assert.Equal(customerId, _account.StripeCustomerId);
            Assert.Equal(customerId, (await _context.Accounts.SingleAsync(a => a.Subject == Subject)).StripeCustomerId);
        }

        [Fact]
        public async Task CreatePortal_WithoutCustomer_Throws409()
        {
            var ex = await Assert.ThrowsAsync<BillingException>(() => _service.CreatePortalSessionAsync(Subject, CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
            Assert.Equal("no_customer", ex.Error);
        }

        [Fact]
        public async Task CreatePortal_WithCustomer_ReturnsReturnUrl()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);

            var url = await _service.CreatePortalSessionAsync(Subject, CancellationToken.None);

            Assert.Equal("https://ec-auth.io/mypage/", url);
        }

        [Fact]
        public async Task GetStatus_Refresh_SyncsPaymentMethodFromStripe()
        {
            SetupReport(orgBillable: true);
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None); // Fake: Checkout 作成で PM が付く

            var before = await _service.GetStatusAsync(Subject, _month, refresh: false, CancellationToken.None);
            var after = await _service.GetStatusAsync(Subject, _month, refresh: true, CancellationToken.None);

            Assert.Null(before!.PaymentMethodRegisteredAt);   // Webhook 未着・refresh なし → キャッシュのまま
            Assert.NotNull(after!.PaymentMethodRegisteredAt);
            Assert.True(after.HasStripeCustomer);
        }

        [Fact]
        public async Task GetStatus_Refresh_ClearsWhenCardRemoved()
        {
            SetupReport(orgBillable: true);
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);
            await _service.GetStatusAsync(Subject, _month, refresh: true, CancellationToken.None);
            _stripe.SetPaymentMethod(_account.StripeCustomerId!, false);

            var status = await _service.GetStatusAsync(Subject, _month, refresh: true, CancellationToken.None);

            Assert.Null(status!.PaymentMethodRegisteredAt);
        }

        // ---- Webhook ----

        private static StripeWebhookEnvelope Event(string id, string type, string? customer, string? mode = null) =>
            new(id, type, customer, mode);

        [Fact]
        public async Task Webhook_SetupCheckoutCompleted_RegistersPaymentMethod()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);

            var processed = await _service.HandleWebhookAsync(
                Tenant, Event("evt_1", "checkout.session.completed", _account.StripeCustomerId, "setup"), CancellationToken.None);

            Assert.True(processed);
            Assert.NotNull(_account.PaymentMethodRegisteredAt);
            Assert.Single(_context.StripeWebhookEvents, e => e.Id == "evt_1" && e.TenantName == Tenant);
        }

        [Fact]
        public async Task Webhook_Redelivery_IsIgnored()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);
            var evt = Event("evt_dup", "checkout.session.completed", _account.StripeCustomerId, "setup");

            var first = await _service.HandleWebhookAsync(Tenant, evt, CancellationToken.None);
            _stripe.SetPaymentMethod(_account.StripeCustomerId!, false);
            var second = await _service.HandleWebhookAsync(Tenant, evt, CancellationToken.None);

            Assert.True(first);
            Assert.False(second);
            Assert.NotNull(_account.PaymentMethodRegisteredAt); // 再送では再同期しない
            Assert.Single(_context.StripeWebhookEvents);
        }

        [Fact]
        public async Task Webhook_TenantMismatch_RecordsButDoesNotTouchAccount()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);

            // live の Customer に対するイベントが test テナント（stg-accounts）の受け口に届いた
            var processed = await _service.HandleWebhookAsync(
                "stg-accounts", Event("evt_x", "checkout.session.completed", _account.StripeCustomerId, "setup"), CancellationToken.None);

            Assert.True(processed);
            Assert.Null(_account.PaymentMethodRegisteredAt);
            Assert.Single(_context.StripeWebhookEvents, e => e.TenantName == "stg-accounts");
        }

        [Fact]
        public async Task Webhook_PaymentCheckoutOrUnrelatedType_IsRecordedOnly()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);

            Assert.True(await _service.HandleWebhookAsync(Tenant, Event("evt_a", "checkout.session.completed", _account.StripeCustomerId, "payment"), CancellationToken.None));
            Assert.True(await _service.HandleWebhookAsync(Tenant, Event("evt_b", "invoice.paid", _account.StripeCustomerId), CancellationToken.None));
            Assert.True(await _service.HandleWebhookAsync(Tenant, Event("evt_c", "customer.updated", null), CancellationToken.None));

            Assert.Null(_account.PaymentMethodRegisteredAt);
            Assert.Equal(3, _context.StripeWebhookEvents.Count());
        }

        [Fact]
        public async Task Webhook_PaymentMethodDetached_ClearsRegistration()
        {
            await _service.CreateCheckoutSessionAsync(Subject, CancellationToken.None);
            await _service.HandleWebhookAsync(Tenant, Event("evt_1", "setup_intent.succeeded", _account.StripeCustomerId), CancellationToken.None);
            Assert.NotNull(_account.PaymentMethodRegisteredAt);

            _stripe.SetPaymentMethod(_account.StripeCustomerId!, false);
            await _service.HandleWebhookAsync(Tenant, Event("evt_2", "payment_method.detached", _account.StripeCustomerId), CancellationToken.None);

            Assert.Null(_account.PaymentMethodRegisteredAt);
        }

        [Fact]
        public async Task Webhook_UnknownCustomer_IsIgnored()
        {
            var processed = await _service.HandleWebhookAsync(Tenant, Event("evt_1", "setup_intent.succeeded", "cus_unknown"), CancellationToken.None);

            Assert.True(processed);
            Assert.Null(_account.PaymentMethodRegisteredAt);
        }

        // ---- 戻り先 URL ----

        [Fact]
        public async Task CreateCheckout_ReturnBaseUrlMissing_Throws()
        {
            var service = new BillingService(
                _context, _tenantService, _accounts.Object, _usage.Object,
                new PricingPlanResolver(_context), new PricingCalculator(), _stripe,
                new ConfigurationBuilder().Build(),
                new Mock<ILogger<BillingService>>().Object);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateCheckoutSessionAsync(Subject, CancellationToken.None));
            Assert.Contains("Billing:ReturnBaseUrl:accounts", ex.Message);
        }

        [Fact]
        public async Task CreateCheckout_HttpReturnBaseUrl_IsRejected()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:ReturnBaseUrl:accounts"] = "http://ec-auth.io" })
                .Build();
            var service = new BillingService(
                _context, _tenantService, _accounts.Object, _usage.Object,
                new PricingPlanResolver(_context), new PricingCalculator(), _stripe, configuration,
                new Mock<ILogger<BillingService>>().Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateCheckoutSessionAsync(Subject, CancellationToken.None));
        }

        public void Dispose() => _context.Dispose();
    }
}
