using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Services.Billing;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace IdentityProvider.Test.Services.Billing
{
    /// <summary>
    /// <see cref="MonthlyInvoicingService"/>: 起票の判断（0 円・請求対象外・支払い方法未登録）、Stripe への起票と明細、
    /// 再実行で二重起票しないこと、途中失敗からの再開、テナントの切り替え、月次ジョブの起動条件。
    /// 金額は <see cref="IBillingService.GetEstimateAsync"/> をモックして与える（計算そのものは BillingServiceTests）。
    /// DB は SQLite（失敗の記録が条件付き <c>ExecuteUpdate</c> のため）。Stripe は <see cref="FakeStripeGateway"/>。
    /// </summary>
    public class MonthlyInvoicingServiceTests : IDisposable
    {
        private const string Live = "accounts";
        private const string Test = "stg-accounts";

        private readonly RetryingSqliteContext _db;
        private readonly EcAuthDbContext _context;
        private readonly MockTenantService _tenantService = new();
        private readonly Mock<IBillingService> _billing = new();
        private readonly FakeStripeGateway _fake = new();
        private readonly FlakyGateway _stripe;
        private readonly BillingOptions _options = new() { InvoicingEnabled = true, InvoicingStartMonth = "2026-11" };
        private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 3, 30, 0, UsageMonth.JstOffset));
        private readonly MonthlyInvoicingService _service;
        private readonly UsageMonth _month = Month("2026-11");

        // GetEstimateAsync が呼ばれたときのテナント（Account のクエリフィルターの前提）
        private readonly Dictionary<string, string> _estimateTenants = new();

        public MonthlyInvoicingServiceTests()
        {
            _db = new RetryingSqliteContext(_tenantService);
            _context = _db.Context;
            _stripe = new FlakyGateway(_fake);

            _context.Organizations.AddRange(
                new Organization { Id = 1, Code = "accounts", Name = "EcAuth", TenantName = Live },
                new Organization { Id = 2, Code = "stg-accounts", Name = "EcAuth stg", TenantName = Test },
                new Organization { Id = 10, Code = "shop", Name = "Shop", TenantName = "shop" });
            _context.SaveChanges();

            _service = new MonthlyInvoicingService(
                _context, _tenantService, _billing.Object, _stripe, Options.Create(_options), _time,
                new Mock<ILogger<MonthlyInvoicingService>>().Object);
        }

        private static UsageMonth Month(string value)
        {
            Assert.True(UsageMonth.TryParse(value, out var month));
            return month;
        }

        /// <summary>Account を作り、owner として Organization 10 を持たせる。</summary>
        private Account AddAccount(string subject, string tenant = Live, bool registered = true, string role = AccountOrganization.OwnerRole)
        {
            var account = new Account
            {
                Subject = subject,
                Email = $"{subject}@example.jp",
                OrganizationId = tenant == Live ? 1 : 2,
                StripeCustomerId = registered ? $"cus_{subject}" : null,
                PaymentMethodRegisteredAt = registered ? DateTimeOffset.UtcNow : null,
            };
            _context.Accounts.Add(account);
            _context.AccountOrganizations.Add(new AccountOrganization { AccountSubject = subject, OrganizationId = 10, Role = role });
            _context.SaveChanges();
            _context.ChangeTracker.Clear();
            return account;
        }

        private static IBillingService.ClientEstimate ClientLine(int id, int mau, long amount, string? exempt = null) =>
            new(id, $"client-{id}", $"App {id}", SubjectType.B2B, mau, 5, Math.Max(0, mau - 5),
                ListPriceJpy: Math.Max(0, mau - 5) * 100L, AmountJpy: amount, IsBillable: exempt == null,
                ExemptReason: exempt, CustomPricing: false);

        private void SetEstimate(string subject, long discount = 0, bool exempt = false, int? discountPercent = null,
            params IBillingService.ClientEstimate[] clients)
        {
            var subtotal = clients.Sum(c => c.AmountJpy);
            var estimate = new IBillingService.Estimate(
                _month, DateTimeOffset.UtcNow, subtotal, discount, subtotal - discount,
                new IBillingService.PlanSummary(exempt, discountPercent, null, false, false),
                new[] { new IBillingService.OrganizationEstimate(10, "shop", "Shop", false, true, subtotal, clients) });
            _billing.Setup(b => b.GetEstimateAsync(subject, _month, It.IsAny<CancellationToken>()))
                .Callback(() => _estimateTenants[subject] = _tenantService.TenantName)
                .ReturnsAsync(estimate);
        }

        private BillingInvoice Row(string subject) =>
            _context.BillingInvoices.AsNoTracking().Include(i => i.Lines)
                .Single(i => i.YearMonth == _month.Value && i.AccountSubject == subject);

        // ---- 起票 ----

        [Fact]
        public async Task Run_RegisteredAccountWithAmount_CreatesDraftInvoiceWithItems()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", discount: 70, discountPercent: 10,
                clients: new[] { ClientLine(1, 12, 700), ClientLine(2, 3, 0), ClientLine(3, 20, 0, IBillingService.ExemptReasons.FirstMonth) });

            var result = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(new IMonthlyInvoicingService.RunResult(_month, 1, 0, 0, 0), result);
            var row = Row("acct-a");
            Assert.Equal(IMonthlyInvoicingService.Statuses.Draft, row.Status);
            Assert.Equal(700, row.SubtotalJpy);
            Assert.Equal(70, row.DiscountJpy);
            Assert.Equal(630, row.TotalJpy);
            Assert.Equal(_time.GetUtcNow().AddDays(2), row.FinalizesAt);
            Assert.Null(row.Error);
            // 内訳は請求対象外の Client も含めて全部残し、割引は負の行
            Assert.Equal(4, row.Lines.Count);
            var discountLine = Assert.Single(row.Lines, l => l.Kind == BillingInvoiceLine.KindDiscount);
            Assert.Equal(-70, discountLine.AmountJpy);
            Assert.Equal("割引（10%）", discountLine.Description);
            Assert.Equal(IBillingService.ExemptReasons.FirstMonth, row.Lines.Single(l => l.ClientDbId == 3).ExemptReason);

            // Stripe には 0 円でない行だけ（税抜）を送る
            var invoice = Assert.Single(_fake.Invoices);
            Assert.Equal(row.StripeInvoiceId, invoice.Id);
            Assert.Equal(Live, invoice.TenantName);
            Assert.Equal("cus_acct-a", invoice.CustomerId);
            Assert.Equal(row.FinalizesAt, invoice.FinalizesAt);
            Assert.Equal("EcAuth ご利用料金（2026 年 11 月分）", invoice.Description);
            Assert.Equal(new[] { 700L, -70L }, invoice.Items.Select(i => i.AmountJpy));
            Assert.Equal(630, invoice.Subtotal);
            Assert.Equal(63, invoice.Tax);
            Assert.Equal("acct-a", invoice.Metadata["ecauth_account_subject"]);
            Assert.All(row.Lines.Where(l => l.AmountJpy != 0), l => Assert.NotNull(l.StripeInvoiceItemId));
            Assert.All(row.Lines.Where(l => l.AmountJpy == 0), l => Assert.Null(l.StripeInvoiceItemId));
        }

        [Fact]
        public async Task Run_SkipsZeroExemptAndNoPaymentMethod_RecordsReasonWithoutStripe()
        {
            AddAccount("acct-zero");
            SetEstimate("acct-zero", clients: ClientLine(1, 5, 0));
            AddAccount("acct-exempt");
            SetEstimate("acct-exempt", exempt: true, clients: ClientLine(1, 30, 0, IBillingService.ExemptReasons.AccountExempt));
            AddAccount("acct-nocard", registered: false);
            SetEstimate("acct-nocard", clients: ClientLine(1, 8, 300));

            var result = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(new IMonthlyInvoicingService.RunResult(_month, 0, 3, 0, 0), result);
            Assert.Equal(IMonthlyInvoicingService.Statuses.SkippedZero, Row("acct-zero").Status);
            Assert.Equal(IMonthlyInvoicingService.Statuses.SkippedExempt, Row("acct-exempt").Status);
            var nocard = Row("acct-nocard");
            Assert.Equal(IMonthlyInvoicingService.Statuses.SkippedNoPaymentMethod, nocard.Status);
            Assert.Equal(300, nocard.TotalJpy);   // 請求しなかった金額も残す
            Assert.Null(nocard.FinalizesAt);
            Assert.Empty(_fake.Invoices);
        }

        [Fact]
        public async Task Run_CustomerWithoutRegisteredCard_IsNoPaymentMethod()
        {
            AddAccount("acct-a");
            await _context.Accounts.IgnoreQueryFilters().Where(a => a.Subject == "acct-a")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PaymentMethodRegisteredAt, (DateTimeOffset?)null));
            SetEstimate("acct-a", clients: ClientLine(1, 8, 300));

            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(IMonthlyInvoicingService.Statuses.SkippedNoPaymentMethod, Row("acct-a").Status);
            Assert.Empty(_fake.Invoices);
        }

        [Fact]
        public async Task Run_Twice_DoesNotInvoiceAgain()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            AddAccount("acct-zero");
            SetEstimate("acct-zero", clients: ClientLine(1, 1, 0));

            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);
            var second = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(new IMonthlyInvoicingService.RunResult(_month, 0, 0, 2, 0), second);
            Assert.Single(_fake.Invoices);
        }

        [Fact]
        public async Task Run_ExistingRowFromAnotherRun_IsLeftAlone()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            // 別インスタンス / ConsoleApp が先に確保した行（UNIQUE で二重に作れない）
            _context.BillingInvoices.Add(new BillingInvoice
            {
                YearMonth = _month.Value, AccountSubject = "acct-a", TenantName = Live, Status = IMonthlyInvoicingService.Statuses.Draft,
            });
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            var result = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(1, result.AlreadyProcessed);
            Assert.Empty(_fake.Invoices);
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                _context.BillingInvoices.Add(new BillingInvoice
                {
                    YearMonth = _month.Value, AccountSubject = "acct-a", TenantName = Live, Status = IMonthlyInvoicingService.Statuses.Pending,
                });
                await _context.SaveChangesAsync();
            });
        }

        [Fact]
        public async Task Run_FailureMidway_LeavesPendingAndResumesWithSameInvoice()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", discount: 100, clients: new[] { ClientLine(1, 12, 700), ClientLine(2, 10, 500) });
            _stripe.FailItemsAfter = 1;   // 1 行目を送った後で落ちる

            var first = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(1, first.Failed);
            var pending = Row("acct-a");
            Assert.Equal(IMonthlyInvoicingService.Statuses.Pending, pending.Status);
            Assert.NotNull(pending.StripeInvoiceId);
            Assert.Contains("Stripe 障害", pending.Error);

            // 金額が後から変わっても、再開は確保した時点の内訳で送る
            SetEstimate("acct-a", clients: ClientLine(1, 50, 4_500));
            _stripe.FailItemsAfter = null;
            var second = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(1, second.Invoiced);
            var row = Row("acct-a");
            Assert.Equal(IMonthlyInvoicingService.Statuses.Draft, row.Status);
            Assert.Null(row.Error);
            var invoice = Assert.Single(_fake.Invoices);
            Assert.Equal(pending.StripeInvoiceId, invoice.Id);
            Assert.Equal(new[] { 700L, 500L, -100L }, invoice.Items.Select(i => i.AmountJpy));
        }

        [Fact]
        public async Task Run_OneAccountFails_OthersAreStillInvoiced()
        {
            AddAccount("acct-a");
            _billing.Setup(b => b.GetEstimateAsync("acct-a", _month, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("account_billing_plan の設定が不正です"));
            AddAccount("acct-b");
            SetEstimate("acct-b", clients: ClientLine(1, 12, 700));

            var result = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(new IMonthlyInvoicingService.RunResult(_month, 1, 0, 0, 1), result);
            Assert.False(_context.BillingInvoices.Any(i => i.AccountSubject == "acct-a"));
            Assert.Equal(IMonthlyInvoicingService.Statuses.Draft, Row("acct-b").Status);
        }

        [Fact]
        public async Task Run_UsesEachAccountsTenant_AndRestoresOriginal()
        {
            AddAccount("acct-live", Live);
            SetEstimate("acct-live", clients: ClientLine(1, 12, 700));
            AddAccount("acct-test", Test);
            SetEstimate("acct-test", clients: ClientLine(1, 12, 700));
            _tenantService.SetTenant("original");

            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(Live, _estimateTenants["acct-live"]);
            Assert.Equal(Test, _estimateTenants["acct-test"]);
            Assert.Equal(Live, _fake.Invoices.Single(i => i.CustomerId == "cus_acct-live").TenantName);
            Assert.Equal(Test, _fake.Invoices.Single(i => i.CustomerId == "cus_acct-test").TenantName);
            Assert.Equal(Test, Row("acct-test").TenantName);
            Assert.Equal("original", _tenantService.TenantName);
        }

        [Fact]
        public async Task Run_Scope_LimitsTenantAndAccount()
        {
            AddAccount("acct-live", Live);
            SetEstimate("acct-live", clients: ClientLine(1, 12, 700));
            AddAccount("acct-test", Test);
            SetEstimate("acct-test", clients: ClientLine(1, 12, 700));
            AddAccount("acct-test2", Test);
            SetEstimate("acct-test2", clients: ClientLine(1, 12, 700));

            await _service.RunAsync(_month, new IMonthlyInvoicingService.Scope(TenantName: Test, AccountSubject: "acct-test"), CancellationToken.None);

            Assert.Equal(new[] { "acct-test" }, _context.BillingInvoices.Select(i => i.AccountSubject).ToArray());
        }

        [Fact]
        public async Task Run_AccountWithoutOwnedOrganization_IsNotBilled()
        {
            AddAccount("acct-member", role: "member");
            SetEstimate("acct-member", clients: ClientLine(1, 12, 700));

            var result = await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(new IMonthlyInvoicingService.RunResult(_month, 0, 0, 0, 0), result);
            Assert.Empty(_context.BillingInvoices);
        }

        [Fact]
        public async Task Plan_DoesNotWrite_AndReportsDecisions()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            AddAccount("acct-nocard", registered: false);
            SetEstimate("acct-nocard", clients: ClientLine(1, 8, 300));

            var plans = await _service.PlanAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            Assert.Equal(IMonthlyInvoicingService.DecisionInvoice, plans.Single(p => p.AccountSubject == "acct-a").Decision);
            Assert.Equal(IMonthlyInvoicingService.Statuses.SkippedNoPaymentMethod, plans.Single(p => p.AccountSubject == "acct-nocard").Decision);
            Assert.All(plans, p => Assert.Null(p.Existing));
            Assert.Empty(_context.BillingInvoices);
            Assert.Empty(_fake.Invoices);
        }

        // ---- 取り消し ----

        [Fact]
        public async Task Cancel_Draft_DeletesInvoiceAndKeepsRow()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            var row = await _service.CancelAsync(_month, "acct-a", CancellationToken.None);

            Assert.Equal(IMonthlyInvoicingService.Statuses.Deleted, row.Status);
            Assert.Equal(StripeInvoiceSnapshot.DeletedStatus, _fake.Invoices.Single().Status);
            Assert.Equal(IMonthlyInvoicingService.Statuses.Deleted, Row("acct-a").Status);
        }

        [Fact]
        public async Task Cancel_Open_Voids()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);
            _fake.SetInvoiceStatus(_fake.Invoices.Single().Id, "open");

            var row = await _service.CancelAsync(_month, "acct-a", CancellationToken.None);

            Assert.Equal(IMonthlyInvoicingService.Statuses.Void, row.Status);
        }

        [Fact]
        public async Task Cancel_SkippedRow_Throws()
        {
            AddAccount("acct-zero");
            SetEstimate("acct-zero", clients: ClientLine(1, 1, 0));
            await _service.RunAsync(_month, IMonthlyInvoicingService.Scope.All, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CancelAsync(_month, "acct-zero", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CancelAsync(_month, "nobody", CancellationToken.None));
        }

        // ---- 月次ジョブの起動条件 ----

        [Theory]
        [InlineData("2026-12-01T02:59:59+09:00", null)]       // 1 日 03:00 JST より前は前月分を起票しない
        [InlineData("2026-12-01T03:00:00+09:00", "2026-11")]
        [InlineData("2026-11-30T18:00:00+00:00", "2026-11")]  // UTC では前日でも JST では 1 日 03:00
        [InlineData("2026-12-15T12:00:00+09:00", "2026-11")]  // 月の途中でも前月分（取りこぼしの追いつき）
        [InlineData("2026-11-20T12:00:00+09:00", null)]       // 前月（10 月）は開始月より前
        [InlineData("2027-01-01T03:00:00+09:00", "2026-12")]  // 年をまたぐ
        public void DueMonth_FollowsScheduleAndStartMonth(string now, string? expected)
        {
            var due = IMonthlyInvoicingService.DueMonth(DateTimeOffset.Parse(now), Month("2026-11"));
            Assert.Equal(expected, due?.Value);
        }

        [Fact]
        public async Task RunIfDue_Disabled_DoesNothing()
        {
            _options.InvoicingEnabled = false;
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));

            Assert.Null(await _service.RunIfDueAsync(_time.GetUtcNow(), CancellationToken.None));
            Assert.Empty(_context.BillingInvoiceRuns);
            Assert.Empty(_fake.Invoices);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("2026/11")]
        public async Task RunIfDue_EnabledWithoutValidStartMonth_Throws(string? startMonth)
        {
            _options.InvoicingStartMonth = startMonth;

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunIfDueAsync(_time.GetUtcNow(), CancellationToken.None));
            Assert.Empty(_context.BillingInvoiceRuns);
        }

        [Fact]
        public async Task RunIfDue_RunsOnceAndMarksCompleted()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));

            var first = await _service.RunIfDueAsync(_time.GetUtcNow(), CancellationToken.None);
            var second = await _service.RunIfDueAsync(_time.GetUtcNow().AddHours(1), CancellationToken.None);

            Assert.NotNull(first);
            Assert.Equal(_month, first.Month);
            Assert.Equal(1, first.Invoiced);
            Assert.Null(second);
            var run = Assert.Single(_context.BillingInvoiceRuns.AsNoTracking());
            Assert.Equal("2026-11", run.YearMonth);
            Assert.NotNull(run.CompletedAt);
            Assert.Equal(1, run.Attempts);
        }

        [Fact]
        public async Task RunIfDue_WithFailure_StaysIncompleteAndRetries()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));
            _stripe.FailItemsAfter = 0;

            var first = await _service.RunIfDueAsync(_time.GetUtcNow(), CancellationToken.None);
            Assert.Equal(1, first!.Failed);
            Assert.Null(_context.BillingInvoiceRuns.AsNoTracking().Single().CompletedAt);

            _stripe.FailItemsAfter = null;
            var second = await _service.RunIfDueAsync(_time.GetUtcNow().AddHours(1), CancellationToken.None);

            Assert.Equal(1, second!.Invoiced);
            var run = _context.BillingInvoiceRuns.AsNoTracking().Single();
            Assert.NotNull(run.CompletedAt);
            Assert.Equal(2, run.Attempts);
            Assert.Equal(0, run.Failed);
        }

        [Fact]
        public async Task RunIfDue_BeforeStartMonth_DoesNothing()
        {
            AddAccount("acct-a");
            SetEstimate("acct-a", clients: ClientLine(1, 12, 700));

            // 2026-11-01 03:30 JST: 前月は 2026-10（開始月 2026-11 より前）
            Assert.Null(await _service.RunIfDueAsync(new DateTimeOffset(2026, 11, 1, 3, 30, 0, UsageMonth.JstOffset), CancellationToken.None));
            Assert.Empty(_context.BillingInvoiceRuns);
        }

        public void Dispose() => _db.Dispose();

        private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        }

        /// <summary>明細の追加を指定件数で失敗させる（Stripe の一時障害の再現）。それ以外は Fake に委ねる。</summary>
        private sealed class FlakyGateway(FakeStripeGateway inner) : IStripeGateway
        {
            private int _items;

            /// <summary>この件数の明細を送った後で失敗する。null なら失敗しない。</summary>
            public int? FailItemsAfter { get; set; }

            public Task<string> AddInvoiceItemAsync(string tenantName, string customerId, string invoiceId, long amountJpy,
                string description, IReadOnlyDictionary<string, string> metadata, string idempotencyKey, CancellationToken cancellationToken)
            {
                if (FailItemsAfter is { } limit && _items >= limit)
                {
                    throw new InvalidOperationException("Stripe 障害（テスト）");
                }
                _items++;
                return inner.AddInvoiceItemAsync(tenantName, customerId, invoiceId, amountJpy, description, metadata, idempotencyKey, cancellationToken);
            }

            public Task<StripeInvoiceSnapshot> CreateInvoiceAsync(string tenantName, InvoiceDraftRequest request, string idempotencyKey, CancellationToken cancellationToken)
                => inner.CreateInvoiceAsync(tenantName, request, idempotencyKey, cancellationToken);
            public Task<StripeInvoiceSnapshot?> GetInvoiceAsync(string tenantName, string invoiceId, CancellationToken cancellationToken)
                => inner.GetInvoiceAsync(tenantName, invoiceId, cancellationToken);
            public Task<StripeInvoiceSnapshot> CancelInvoiceAsync(string tenantName, string invoiceId, CancellationToken cancellationToken)
                => inner.CancelInvoiceAsync(tenantName, invoiceId, cancellationToken);
            public Task<string> CreateCustomerAsync(string tenantName, string email, string? name, IReadOnlyDictionary<string, string> metadata, string idempotencyKey, CancellationToken cancellationToken)
                => inner.CreateCustomerAsync(tenantName, email, name, metadata, idempotencyKey, cancellationToken);
            public Task<string> CreateSetupCheckoutSessionAsync(string tenantName, string customerId, string successUrl, string cancelUrl, CancellationToken cancellationToken)
                => inner.CreateSetupCheckoutSessionAsync(tenantName, customerId, successUrl, cancelUrl, cancellationToken);
            public Task<string> CreatePortalSessionAsync(string tenantName, string customerId, string returnUrl, CancellationToken cancellationToken)
                => inner.CreatePortalSessionAsync(tenantName, customerId, returnUrl, cancellationToken);
            public Task<PaymentMethodState> EnsureDefaultPaymentMethodAsync(string tenantName, string customerId, CancellationToken cancellationToken)
                => inner.EnsureDefaultPaymentMethodAsync(tenantName, customerId, cancellationToken);
            public StripeWebhookEnvelope ParseWebhookEvent(string tenantName, string payload, string? signatureHeader)
                => inner.ParseWebhookEvent(tenantName, payload, signatureHeader);
        }
    }
}
