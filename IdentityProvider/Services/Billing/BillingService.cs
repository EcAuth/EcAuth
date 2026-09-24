using System.Text.RegularExpressions;
using IdentityProvider.Data;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services.Billing
{
    /// <inheritdoc cref="IBillingService" />
    public sealed class BillingService : IBillingService
    {
        private static readonly Regex NonConfigKeyChar = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

        /// <summary>支払い方法の状態を変えうるイベント。それ以外の種別は記録だけして無視する。</summary>
        private static readonly HashSet<string> PaymentMethodEvents = new(StringComparer.Ordinal)
        {
            "checkout.session.completed",
            "setup_intent.succeeded",
            "customer.updated",
            "payment_method.attached",
            "payment_method.detached",
        };

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IAccountService _accountService;
        private readonly IUsageReportService _usageReport;
        private readonly IPricingPlanResolver _plans;
        private readonly IPricingCalculator _pricing;
        private readonly IStripeGateway _stripe;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BillingService> _logger;

        public BillingService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IAccountService accountService,
            IUsageReportService usageReport,
            IPricingPlanResolver plans,
            IPricingCalculator pricing,
            IStripeGateway stripe,
            IConfiguration configuration,
            ILogger<BillingService> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _accountService = accountService;
            _usageReport = usageReport;
            _plans = plans;
            _pricing = pricing;
            _stripe = stripe;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IBillingService.Status?> GetStatusAsync(
            string accountSubject, UsageMonth month, bool refresh, CancellationToken cancellationToken)
        {
            var account = await _accountService.GetBySubjectAsync(accountSubject);
            if (account == null)
            {
                return null;
            }

            if (refresh && account.StripeCustomerId != null)
            {
                await SyncPaymentMethodAsync(account, cancellationToken);
            }

            return new IBillingService.Status(
                account.StripeCustomerId != null,
                account.PaymentMethodRegisteredAt,
                await BuildEstimateAsync(accountSubject, month, cancellationToken));
        }

        /// <inheritdoc />
        public async Task<IBillingService.Estimate?> GetEstimateAsync(
            string accountSubject, UsageMonth month, CancellationToken cancellationToken)
        {
            var account = await _accountService.GetBySubjectAsync(accountSubject);
            if (account == null)
            {
                return null;
            }

            return await BuildEstimateAsync(accountSubject, month, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string?> CreateCheckoutSessionAsync(string accountSubject, CancellationToken cancellationToken)
        {
            var account = await _accountService.GetBySubjectAsync(accountSubject);
            if (account == null)
            {
                return null;
            }

            var tenant = _tenantService.TenantName;
            var customerId = await EnsureCustomerAsync(account, tenant, cancellationToken);
            var returnBase = ReturnBaseUrl(tenant);

            // 戻り先はマイページ。成功時のクエリでフロントが再同期（refresh=1）を掛ける。
            return await _stripe.CreateSetupCheckoutSessionAsync(
                tenant,
                customerId,
                successUrl: $"{returnBase}/mypage/?billing=setup_complete",
                cancelUrl: $"{returnBase}/mypage/?billing=setup_cancelled",
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string?> CreatePortalSessionAsync(string accountSubject, CancellationToken cancellationToken)
        {
            var account = await _accountService.GetBySubjectAsync(accountSubject);
            if (account == null)
            {
                return null;
            }

            if (account.StripeCustomerId == null)
            {
                throw new BillingException(
                    StatusCodes.Status409Conflict, "no_customer",
                    "支払い方法が未登録です。先に支払い方法を登録してください。");
            }

            var tenant = _tenantService.TenantName;
            return await _stripe.CreatePortalSessionAsync(
                tenant, account.StripeCustomerId, $"{ReturnBaseUrl(tenant)}/mypage/", cancellationToken);
        }

        /// <inheritdoc />
        public async Task<bool> HandleWebhookAsync(
            string tenantName, StripeWebhookEnvelope envelope, CancellationToken cancellationToken)
        {
            // 冪等化: イベント ID を主キーにした行を先に入れ、処理が終わったら processed_at を立てる。
            // 完了済み（processed_at != null）なら再送として無視。行はあるが未完了なら前回の処理が失敗して
            // Stripe が再送してきた（または処理中）なので、もう一度処理する。行を消さないのは監査のため。
            var existing = await _context.StripeWebhookEvents
                .FirstOrDefaultAsync(e => e.Id == envelope.Id, cancellationToken);
            if (existing?.ProcessedAt != null)
            {
                _logger.LogInformation("Stripe Webhook の再送を無視しました: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
                return false;
            }

            var record = existing;
            if (record == null)
            {
                record = new StripeWebhookEvent
                {
                    Id = envelope.Id,
                    Type = envelope.Type,
                    TenantName = tenantName,
                    ReceivedAt = DateTimeOffset.UtcNow,
                };
                _context.StripeWebhookEvents.Add(record);
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (DatabaseResilience.IsUniqueConstraintViolation(ex))
                {
                    // 存在確認と INSERT の間に同じイベントが並行して届いた。相手側が処理する（失敗すれば再送で戻る）。
                    _context.ChangeTracker.Clear();
                    _logger.LogInformation("Stripe Webhook の並行配信を無視しました: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
                    return false;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Stripe Webhook を再処理します（前回は未完了）: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
            }

            // ここから先で例外が出ると 500 になり、processed_at が null のまま残る → Stripe の再送で再処理される。
            await ProcessAsync(tenantName, envelope, cancellationToken);

            record.ProcessedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        /// <summary>イベントの本処理。関係ないイベントは何もしないで戻る（それも「処理完了」）。</summary>
        private async Task ProcessAsync(string tenantName, StripeWebhookEnvelope envelope, CancellationToken cancellationToken)
        {
            if (!PaymentMethodEvents.Contains(envelope.Type) || envelope.CustomerId == null)
            {
                return;
            }

            if (envelope.Type == "checkout.session.completed" && envelope.CheckoutMode != "setup")
            {
                return;
            }

            // Webhook はテナントのクエリフィルター外から Account を引く。live / test の取り違え防止に、
            // Customer が「そのテナントの受付 Organization に属する Account」のものであることも条件にする。
            var account = await _context.Accounts
                .IgnoreQueryFilters()
                .Include(a => a.Organization)
                .FirstOrDefaultAsync(
                    a => a.StripeCustomerId == envelope.CustomerId
                        && a.Organization != null
                        && a.Organization.TenantName == tenantName,
                    cancellationToken);
            if (account == null)
            {
                _logger.LogWarning(
                    "Stripe Webhook の Customer に対応する Account がありません: Event={EventId}, Type={Type}, Customer={CustomerId}, Tenant={Tenant}",
                    envelope.Id, envelope.Type, envelope.CustomerId, tenantName);
                return;
            }

            await SyncPaymentMethodAsync(account, cancellationToken);
        }

        /// <summary>Stripe Customer が無ければ作って保存する。冪等キーは Account 単位。</summary>
        private async Task<string> EnsureCustomerAsync(Account account, string tenant, CancellationToken cancellationToken)
        {
            if (account.StripeCustomerId != null)
            {
                return account.StripeCustomerId;
            }

            var customerId = await _stripe.CreateCustomerAsync(
                tenant,
                account.Email,
                account.DisplayName,
                new Dictionary<string, string>
                {
                    ["ecauth_account_subject"] = account.Subject,
                    ["ecauth_tenant"] = tenant,
                },
                idempotencyKey: $"customer:{tenant}:{account.Subject}",
                cancellationToken);

            account.StripeCustomerId = customerId;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Stripe Customer を作成しました: Account={Subject}, Customer={CustomerId}", account.Subject, customerId);
            return customerId;
        }

        /// <summary>Stripe の既定の支払い方法の有無を <c>payment_method_registered_at</c> に反映する。</summary>
        private async Task SyncPaymentMethodAsync(Account account, CancellationToken cancellationToken)
        {
            var tenant = account.Organization?.TenantName ?? _tenantService.TenantName;
            var has = await _stripe.EnsureDefaultPaymentMethodAsync(tenant, account.StripeCustomerId!, cancellationToken);

            if (has && account.PaymentMethodRegisteredAt == null)
            {
                account.PaymentMethodRegisteredAt = DateTimeOffset.UtcNow;
            }
            else if (!has && account.PaymentMethodRegisteredAt != null)
            {
                account.PaymentMethodRegisteredAt = null;
            }
            else
            {
                return;
            }

            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "支払い方法の登録状態を更新しました: Account={Subject}, Registered={Registered}",
                account.Subject, has);
        }

        private async Task<IBillingService.Estimate> BuildEstimateAsync(
            string accountSubject, UsageMonth month, CancellationToken cancellationToken)
        {
            // 管理対象 Organization は削除済みも含めて引く。IAccountService.GetManagedOrganizationsAsync は
            // トークンの managed_orgs 用に「現在削除されていないもの」へ絞るが、請求は「対象月に有効だったか」で
            // 見る（UsageReportService.IsBillable）。月の途中で解約したサイトの当月 MAU を落とさないため。
            var organizationIds = await _context.AccountOrganizations
                .IgnoreQueryFilters()
                .Where(ao => ao.AccountSubject == accountSubject)
                .Select(ao => ao.OrganizationId)
                .ToListAsync(cancellationToken);
            var report = await _usageReport.GetReportAsync(
                new IUsageReportService.UsageReportQuery(month, organizationIds.ToHashSet(), IncludeNonBillable: true),
                cancellationToken);
            var plan = await _plans.ResolveAsync(accountSubject, month, cancellationToken);
            return BuildEstimate(report, plan);
        }

        /// <summary>
        /// 集計結果と課金条件から見込み額を組む。純関数（DB・Stripe に触らない）。月次起票も同じ関数で金額を出す。
        /// <para>
        /// 請求対象外の判定順: Account 全体の除外 → Organization（サンドボックス / 内部 / 削除済み）→ Client の除外 →
        /// 初月無料（<c>client.created_at</c> が対象月の中）。対象外でも料金表どおりの金額（<c>ListPriceJpy</c>）は見せ、
        /// 請求見込み（<c>AmountJpy</c>）だけ 0 にする。割引は請求対象 Client の合計に対して 1 回。
        /// </para>
        /// </summary>
        public IBillingService.Estimate BuildEstimate(IUsageReportService.UsageReport report, PricingPlan plan)
        {
            var monthStart = report.Month.Start;
            var monthEnd = monthStart.AddMonths(1);

            var organizations = new List<IBillingService.OrganizationEstimate>(report.Organizations.Count);
            long subtotal = 0;
            foreach (var org in report.Organizations)
            {
                var clients = new List<IBillingService.ClientEstimate>(org.Clients.Count);
                long orgTotal = 0;
                foreach (var client in org.Clients)
                {
                    var quote = _pricing.Calculate(plan, client.SubjectType, client.MonthlyActiveUsers);
                    var reason = ExemptReason(plan, org, client, monthStart, monthEnd);
                    var amount = reason == null ? quote.AmountJpy : 0;
                    clients.Add(new IBillingService.ClientEstimate(
                        client.Id, client.ClientId, client.AppName, client.SubjectType,
                        client.MonthlyActiveUsers, quote.FreeTierMau, quote.BillableUnits,
                        ListPriceJpy: quote.AmountJpy,
                        AmountJpy: amount,
                        IsBillable: reason == null,
                        ExemptReason: reason,
                        CustomPricing: quote.CustomPricing));
                    orgTotal += amount;
                }
                organizations.Add(new IBillingService.OrganizationEstimate(
                    org.OrganizationId, org.Code, org.Name, org.IsSandbox, org.IsBillable, orgTotal, clients));
                subtotal += orgTotal;
            }

            var discount = _pricing.CalculateDiscount(plan, subtotal);
            return new IBillingService.Estimate(
                report.Month,
                report.AsOf,
                subtotal,
                discount,
                subtotal - discount,
                new IBillingService.PlanSummary(
                    plan.Exempt, plan.DiscountPercent, plan.DiscountJpy,
                    plan.B2BTiers != null, plan.B2CTiers != null),
                organizations);
        }

        private static string? ExemptReason(
            PricingPlan plan,
            IUsageReportService.OrganizationUsage org,
            IUsageReportService.ClientUsage client,
            DateTimeOffset monthStart,
            DateTimeOffset monthEnd)
        {
            if (plan.Exempt)
            {
                return IBillingService.ExemptReasons.AccountExempt;
            }
            if (!org.IsBillable)
            {
                return IBillingService.ExemptReasons.OrganizationNotBillable;
            }
            if (client.BillingExempt)
            {
                return IBillingService.ExemptReasons.ClientExempt;
            }
            // 初月無料: 月末に使い始めた顧客が数日分で 1 か月分の MAU 単価を払う不公平を避ける（EcAuthDocs#119）。
            if (client.CreatedAt >= monthStart && client.CreatedAt < monthEnd)
            {
                return IBillingService.ExemptReasons.FirstMonth;
            }
            return null;
        }

        /// <summary>
        /// Checkout / Portal からの戻り先の基底 URL。<c>Billing:ReturnBaseUrl:{tenant}</c>（https のみ）。
        /// Host ヘッダへのフォールバックはしない（MagicLink:BaseUrl と同じ理由）。
        /// </summary>
        private string ReturnBaseUrl(string tenantName)
        {
            var key = $"Billing:ReturnBaseUrl:{NonConfigKeyChar.Replace(tenantName, "_")}";
            var configured = _configuration[key];
            if (string.IsNullOrWhiteSpace(configured)
                || !Uri.TryCreate(configured, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError("課金の戻り先 URL が未設定または不正です: Tenant={Tenant}, Key={Key}", tenantName, key);
                throw new InvalidOperationException($"{key} に有効な https:// URL を設定してください。");
            }
            return configured.TrimEnd('/');
        }
    }
}
