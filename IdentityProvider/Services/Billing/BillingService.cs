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
            // Customer がダッシュボード / API で削除された。紐付けを外さないと、以後の Checkout / Portal が
            // 削除済み ID を Stripe に送り続けて失敗し、Customer を作り直す経路も無くなる。
            "customer.deleted",
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

            // 保存済みの Customer が Stripe 側で削除されていないか確かめる。customer.deleted の Webhook を取りこぼした
            //（エンドポイント設定前の削除、再送切れ）場合、確かめずに渡すと Checkout が失敗し続け、作り直す経路も無くなる。
            // 削除済みなら同期が紐付けを外すので、下の EnsureCustomerAsync が新しい Customer を作る。
            if (account.StripeCustomerId != null)
            {
                await SyncPaymentMethodAsync(account, cancellationToken);
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

            // Portal を作る前に Stripe と 1 回同期する。DB の状態だけで判定すると次を取りこぼす:
            // - 登録直後で Webhook がまだ届いていない（DB は未登録だが実際は登録済み）
            // - Customer が Stripe 側で削除され、その Webhook を取りこぼした（DB は登録済みだが削除済みの ID を渡すことになる）
            await SyncPaymentMethodAsync(account, cancellationToken);
            if (account.StripeCustomerId == null)
            {
                // 同期で Customer が Stripe 側で削除済みとわかった
                throw new BillingException(
                    StatusCodes.Status409Conflict, "no_customer",
                    "支払い方法が未登録です。先に支払い方法を登録してください。");
            }

            // Customer はあるがカードが無い: Checkout をキャンセルした（Customer だけ残る）など
            if (account.PaymentMethodRegisteredAt == null)
            {
                throw new BillingException(
                    StatusCodes.Status409Conflict, "no_payment_method",
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
            //
            // 保証するのは「完了済みのイベントを再処理しない」ことまで。未完了の間に同じイベントが並行して届くと
            // ProcessAsync が二重に走りうる。イベント単位のロックは置かず、代わりに ProcessAsync を
            // 「Stripe の現在の状態を読み直して DB に写す」冪等な処理に限ることで結果を一致させる。
            // 別イベント同士（カード追加と削除など）が並行したときに古い読み取りが新しい結果を上書きしないよう、
            // 書き込みは読み取り開始時刻で条件付きにする（SyncPaymentMethodAsync）。本処理を足すときはこの規約を守ること。
            StripeWebhookEvent? record = null;
            for (var attempt = 0; record == null; attempt++)
            {
                var existing = await _context.StripeWebhookEvents
                    .FirstOrDefaultAsync(e => e.Id == envelope.Id, cancellationToken);
                if (existing?.ProcessedAt != null)
                {
                    _logger.LogInformation("Stripe Webhook の再送を無視しました: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
                    return false;
                }
                if (existing != null)
                {
                    // 前回の処理が失敗した再送、または並行配信の処理中。どちらもこちらで処理してから応答する
                    //（先に 200 を返すと、相手が失敗したとき Stripe が再送を止めて行が未完了のまま残る）。
                    _logger.LogWarning(
                        "Stripe Webhook を処理します（既存の行は未完了）: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
                    record = existing;
                    break;
                }

                var added = new StripeWebhookEvent
                {
                    Id = envelope.Id,
                    Type = envelope.Type,
                    TenantName = tenantName,
                    ReceivedAt = DateTimeOffset.UtcNow,
                };
                _context.StripeWebhookEvents.Add(added);
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    record = added;
                }
                catch (DbUpdateException ex) when (attempt == 0 && DatabaseResilience.IsUniqueConstraintViolation(ex))
                {
                    // 存在確認と INSERT の間に同じイベントが並行して届いた。自分の行を捨てて存在確認からやり直す
                    //（相手の行が未完了なら上の分岐でこちらも処理する）。2 回目も衝突するなら例外 → 500 → Stripe が再送。
                    _context.Entry(added).State = EntityState.Detached;
                    _logger.LogInformation("Stripe Webhook の並行配信を検出しました: Event={EventId}, Type={Type}", envelope.Id, envelope.Type);
                }
            }

            // ここから先で例外が出ると 500 になり、processed_at が null のまま残る → Stripe の再送で再処理される。
            await ProcessAsync(tenantName, envelope, cancellationToken);

            record.ProcessedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        /// <summary>
        /// イベントの本処理。関係ないイベントは何もしないで戻る（それも「処理完了」）。
        /// 二重に実行されても結果が変わらないこと（イベントの中身ではなく Stripe の現在の状態から DB を決めること）が前提。
        /// </summary>
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

        /// <summary>
        /// Stripe Customer が無ければ作って保存する。
        /// 冪等キーは Account と <c>updated_at</c> から作る。同じ行を読んだ並行リクエストや、Customer 作成後に保存だけ
        /// 失敗したやり直しは同じキー（= 同じ Customer）になる。Customer が Stripe 側で削除されて紐付けを外した後は
        /// <c>updated_at</c> が進んでいるので別のキーになり、新しい Customer が作られる（Stripe の冪等キーは 24 時間
        /// 有効なので、Account だけのキーだと削除直後の作り直しで削除済みの Customer が返ってくる）。
        /// </summary>
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
                idempotencyKey: $"customer:{tenant}:{account.Subject}:{account.UpdatedAt.UtcTicks}",
                cancellationToken);

            account.StripeCustomerId = customerId;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Stripe Customer を作成しました: Account={Subject}, Customer={CustomerId}", account.Subject, customerId);
            return customerId;
        }

        /// <summary>
        /// Stripe の現在の状態を <c>payment_method_registered_at</c>（と Customer の紐付け）に反映する。
        /// <para>
        /// 書き込みは <c>payment_method_checked_ticks</c> で条件付きにする: Stripe を読み始めた時刻より新しい読み取りが
        /// 既に反映されていれば、この結果は古いので書かない。これが無いと、カード追加の同期が「あり」を読んだ直後に
        /// カードが外れ、削除の同期が「なし・変更不要」で戻った後に、追加側が「登録済み」を保存してしまう。
        /// 最後に書かれるのは常に最も遅く読み始めた同期の結果になり、最後の変更の Webhook は変更後に読み始めるので、
        /// 最終状態は Stripe と一致する。変更が無くても時刻は必ず書く（「読んだ」ことを後続の古い書き込みに伝えるため）。
        /// </para>
        /// <para>
        /// [前提] 時刻は各インスタンスの時計。App Service のインスタンス間のずれは Webhook の間隔（秒単位）より十分小さい。
        /// Customer の ID も条件に含め、紐付けが変わった後（削除 → 作り直し）の古い同期で新しい Customer を消さない。
        /// </para>
        /// </summary>
        private async Task SyncPaymentMethodAsync(Account account, CancellationToken cancellationToken)
        {
            var customerId = account.StripeCustomerId!;
            var tenant = account.Organization?.TenantName ?? _tenantService.TenantName;
            var readTicks = DateTimeOffset.UtcNow.UtcTicks;
            var state = await _stripe.EnsureDefaultPaymentMethodAsync(tenant, customerId, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            var target = _context.Accounts
                .IgnoreQueryFilters()
                .Where(a => a.Id == account.Id
                    && a.StripeCustomerId == customerId
                    && (a.PaymentMethodCheckedTicks == null || a.PaymentMethodCheckedTicks < readTicks));

            var updated = state switch
            {
                PaymentMethodState.Registered => await target.ExecuteUpdateAsync(s => s
                    // 既に登録済みなら最初に登録した時刻を保つ
                    .SetProperty(a => a.PaymentMethodRegisteredAt, a => a.PaymentMethodRegisteredAt ?? now)
                    .SetProperty(a => a.PaymentMethodCheckedTicks, readTicks)
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken),
                PaymentMethodState.NotRegistered => await target.ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.PaymentMethodRegisteredAt, (DateTimeOffset?)null)
                    .SetProperty(a => a.PaymentMethodCheckedTicks, readTicks)
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken),
                PaymentMethodState.CustomerDeleted => await target.ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.StripeCustomerId, (string?)null)
                    .SetProperty(a => a.PaymentMethodRegisteredAt, (DateTimeOffset?)null)
                    .SetProperty(a => a.PaymentMethodCheckedTicks, readTicks)
                    .SetProperty(a => a.UpdatedAt, now), cancellationToken),
                _ => throw new InvalidOperationException($"未知の PaymentMethodState: {state}"),
            };

            // ExecuteUpdate は追跡中のエンティティを更新しないので読み直す（呼び出し側がこのインスタンスを見て判定する）。
            await _context.Entry(account).ReloadAsync(cancellationToken);

            if (updated == 0)
            {
                _logger.LogInformation(
                    "より新しい同期が反映済みのため、支払い方法の状態を書きませんでした: Account={Subject}, State={State}",
                    account.Subject, state);
                return;
            }

            _logger.LogInformation(
                "支払い方法の状態を同期しました: Account={Subject}, State={State}, Registered={Registered}",
                account.Subject, state, account.PaymentMethodRegisteredAt != null);
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
