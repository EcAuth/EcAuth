using System.Globalization;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IdentityProvider.Services.Billing
{
    /// <inheritdoc cref="IMonthlyInvoicingService" />
    public sealed class MonthlyInvoicingService : IMonthlyInvoicingService
    {
        private const int MaxDescriptionLength = 500;
        private const int MaxErrorLength = 1000;

        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly IBillingService _billing;
        private readonly IStripeGateway _stripe;
        private readonly IOptions<BillingOptions> _options;
        private readonly TimeProvider _time;
        private readonly ILogger<MonthlyInvoicingService> _logger;

        public MonthlyInvoicingService(
            EcAuthDbContext context,
            ITenantService tenantService,
            IBillingService billing,
            IStripeGateway stripe,
            IOptions<BillingOptions> options,
            TimeProvider time,
            ILogger<MonthlyInvoicingService> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _billing = billing;
            _stripe = stripe;
            _options = options;
            _time = time;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<IMonthlyInvoicingService.AccountPlan>> PlanAsync(
            UsageMonth month, IMonthlyInvoicingService.Scope scope, CancellationToken cancellationToken)
        {
            var plans = new List<IMonthlyInvoicingService.AccountPlan>();
            await ForEachAccountAsync(scope, async account =>
            {
                var existing = await _context.BillingInvoices
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.YearMonth == month.Value && i.AccountSubject == account.Subject, cancellationToken);
                plans.Add(await BuildPlanAsync(account, month, existing, cancellationToken));
            }, cancellationToken);
            return plans;
        }

        /// <inheritdoc />
        public async Task<IMonthlyInvoicingService.RunResult> RunAsync(
            UsageMonth month, IMonthlyInvoicingService.Scope scope, CancellationToken cancellationToken)
        {
            int invoiced = 0, skipped = 0, already = 0, failed = 0;
            await ForEachAccountAsync(scope, async account =>
            {
                try
                {
                    var row = await _context.BillingInvoices
                        .Include(i => i.Lines)
                        .FirstOrDefaultAsync(i => i.YearMonth == month.Value && i.AccountSubject == account.Subject, cancellationToken);
                    if (row != null && row.Status != IMonthlyInvoicingService.Statuses.Pending)
                    {
                        already++;
                        return;
                    }

                    row ??= await ClaimAsync(account, month, cancellationToken);
                    if (row == null)
                    {
                        // 別の実行（複数インスタンス・ConsoleApp）が先に行を確保した。その実行に任せる。
                        already++;
                        return;
                    }

                    if (IMonthlyInvoicingService.Statuses.IsSkipped(row.Status))
                    {
                        skipped++;
                        return;
                    }

                    await IssueAsync(row, month, cancellationToken);
                    invoiced++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    _logger.LogError(ex, "月次請求の起票に失敗しました: Month={Month}, Account={Subject}", month, account.Subject);
                    await RecordErrorAsync(month, account.Subject, ex, cancellationToken);
                }
            }, cancellationToken);

            var result = new IMonthlyInvoicingService.RunResult(month, invoiced, skipped, already, failed);
            _logger.LogInformation(
                "月次請求を実行しました: Month={Month}, Invoiced={Invoiced}, Skipped={Skipped}, AlreadyProcessed={Already}, Failed={Failed}",
                month, invoiced, skipped, already, failed);
            return result;
        }

        /// <inheritdoc />
        public async Task<BillingInvoice> CancelAsync(UsageMonth month, string accountSubject, CancellationToken cancellationToken)
        {
            var row = await _context.BillingInvoices
                .FirstOrDefaultAsync(i => i.YearMonth == month.Value && i.AccountSubject == accountSubject, cancellationToken)
                ?? throw new InvalidOperationException($"{month} の起票の記録がありません: Account={accountSubject}");
            if (row.StripeInvoiceId == null)
            {
                throw new InvalidOperationException(
                    $"{month} は Stripe に起票していません（status={row.Status}）: Account={accountSubject}");
            }

            var snapshot = await _stripe.CancelInvoiceAsync(row.TenantName, row.StripeInvoiceId, cancellationToken);
            row.Status = snapshot.Status;
            row.UpdatedAt = _time.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "月次請求を取り消しました: Month={Month}, Account={Subject}, Invoice={InvoiceId}, Status={Status}",
                month, accountSubject, row.StripeInvoiceId, row.Status);
            return row;
        }

        /// <inheritdoc />
        public async Task<IMonthlyInvoicingService.RunResult?> RunIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var options = _options.Value;
            if (!options.InvoicingEnabled)
            {
                return null;
            }

            if (!UsageMonth.TryParse(options.InvoicingStartMonth, out var startMonth))
            {
                // 下限なしで動かすと、有効化した時点の前月（請求するつもりの無い月）まで起票してしまう。
                throw new InvalidOperationException(
                    $"{BillingOptions.SectionName}:InvoicingStartMonth に起票を始める月（YYYY-MM）を設定してください（現在値: '{options.InvoicingStartMonth}'）。");
            }

            var due = IMonthlyInvoicingService.DueMonth(now, startMonth);
            if (due is not { } month)
            {
                return null;
            }

            // 実行記録は ExecuteUpdate で書くので、追跡中の古い値を読まないよう AsNoTracking で引く。
            var run = await _context.BillingInvoiceRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.YearMonth == month.Value, cancellationToken);
            if (run?.CompletedAt != null)
            {
                return null;
            }

            if (run == null)
            {
                run = new BillingInvoiceRun { YearMonth = month.Value, StartedAt = now, UpdatedAt = now };
                _context.BillingInvoiceRuns.Add(run);
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // 別インスタンスが同時に始めた。どちらが処理しても Account 単位の UNIQUE で二重起票はしないので、
                    // 相手の行を使って続行する（見つからなければ本物の失敗なので投げる）。
                    if (!await _context.BillingInvoiceRuns.AsNoTracking().AnyAsync(r => r.YearMonth == month.Value, cancellationToken))
                    {
                        throw;
                    }
                }
                finally
                {
                    _context.Entry(run).State = EntityState.Detached;
                }
            }

            var result = await RunAsync(month, IMonthlyInvoicingService.Scope.All, cancellationToken);

            // 完了にするのは pending の行が 1 件も残っていないときだけ。この実行で失敗が無くても、別インスタンスが
            // 先に確保した行（この実行では AlreadyProcessed に数える）が処理中のまま落ちていると pending で残り、
            // ここで完了にすると以後の確認が再開しなくなる。
            var hasPending = await _context.BillingInvoices
                .AsNoTracking()
                .AnyAsync(i => i.YearMonth == month.Value && i.Status == IMonthlyInvoicingService.Statuses.Pending, cancellationToken);

            // RunAsync は失敗時に ChangeTracker を捨てるので、追跡中の run は当てにせず直接書く。
            var finishedAt = _time.GetUtcNow();
            DateTimeOffset? completedAt = result.Failed == 0 && !hasPending ? finishedAt : null;
            await _context.BillingInvoiceRuns
                .Where(r => r.YearMonth == month.Value)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Attempts, r => r.Attempts + 1)
                    .SetProperty(r => r.Failed, result.Failed)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    .SetProperty(r => r.UpdatedAt, finishedAt), cancellationToken);
            return result;
        }

        /// <summary>
        /// 請求先になる Account（受付テナントに属し、owner として管理する Organization を 1 つ以上持つ）を
        /// テナントごとに列挙する。見込み額の集計（<see cref="IBillingService.GetEstimateAsync"/>）がリクエストの
        /// テナント（Account のクエリフィルター）を前提にしているため、テナントを切り替えて呼ぶ。終わったら元に戻す。
        /// </summary>
        private async Task ForEachAccountAsync(
            IMonthlyInvoicingService.Scope scope, Func<Account, Task> action, CancellationToken cancellationToken)
        {
            var tenants = await _context.Accounts
                .IgnoreQueryFilters()
                .Where(a => a.Organization != null && a.Organization.TenantName != null && a.Organization.DeletedAt == null)
                .Select(a => a.Organization!.TenantName!)
                .Distinct()
                .ToListAsync(cancellationToken);
            tenants.Sort(StringComparer.Ordinal);

            // owner の集合は別クエリで引く。IgnoreQueryFilters はサブクエリに付けてもクエリ全体に効くため、
            // Account のクエリに Any(...) で埋め込むとテナントの絞り込みまで外れて、別テナントの Account を拾う。
            var owners = (await _context.AccountOrganizations
                .IgnoreQueryFilters()
                .Where(ao => ao.Role == AccountOrganization.OwnerRole)
                .Select(ao => ao.AccountSubject)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

            var original = _tenantService.TenantName;
            try
            {
                foreach (var tenant in tenants)
                {
                    if (scope.TenantName != null && tenant != scope.TenantName)
                    {
                        continue;
                    }

                    _tenantService.SetTenant(tenant);
                    var accounts = await _context.Accounts
                        .Include(a => a.Organization)
                        .Where(a => scope.AccountSubject == null || a.Subject == scope.AccountSubject)
                        .OrderBy(a => a.Id)
                        .ToListAsync(cancellationToken);

                    foreach (var account in accounts.Where(a => owners.Contains(a.Subject)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await action(account);
                    }
                }
            }
            finally
            {
                _tenantService.SetTenant(original);
            }
        }

        private async Task<IMonthlyInvoicingService.AccountPlan> BuildPlanAsync(
            Account account, UsageMonth month, BillingInvoice? existing, CancellationToken cancellationToken)
        {
            var estimate = await _billing.GetEstimateAsync(account.Subject, month, cancellationToken)
                ?? throw new InvalidOperationException($"Account が見つかりません: {account.Subject}");
            var registered = account.StripeCustomerId != null && account.PaymentMethodRegisteredAt != null;
            return new IMonthlyInvoicingService.AccountPlan(
                account.Subject,
                account.Organization!.TenantName!,
                account.Email,
                account.StripeCustomerId,
                registered,
                Decide(estimate, registered),
                estimate,
                existing);
        }

        /// <summary>起票するか、どの理由で起票しないか。判定の順序は Account 全体の除外 → 0 円 → 支払い方法。</summary>
        private static string Decide(IBillingService.Estimate estimate, bool paymentMethodRegistered)
        {
            if (estimate.Plan.Exempt)
            {
                return IMonthlyInvoicingService.Statuses.SkippedExempt;
            }
            if (estimate.TotalJpy <= 0)
            {
                return IMonthlyInvoicingService.Statuses.SkippedZero;
            }
            if (!paymentMethodRegistered)
            {
                return IMonthlyInvoicingService.Statuses.SkippedNoPaymentMethod;
            }
            return IMonthlyInvoicingService.DecisionInvoice;
        }

        /// <summary>
        /// Account の当月分の行を確保する（金額と内訳はここで固定）。起票しない判断なら skipped_* で確定させる。
        /// 別の実行が先に確保していたら null。
        /// </summary>
        private async Task<BillingInvoice?> ClaimAsync(Account account, UsageMonth month, CancellationToken cancellationToken)
        {
            var plan = await BuildPlanAsync(account, month, existing: null, cancellationToken);
            var now = _time.GetUtcNow();
            var invoice = plan.Decision == IMonthlyInvoicingService.DecisionInvoice;
            var row = new BillingInvoice
            {
                YearMonth = month.Value,
                AccountSubject = account.Subject,
                TenantName = plan.TenantName,
                StripeCustomerId = account.StripeCustomerId,
                Status = invoice ? IMonthlyInvoicingService.Statuses.Pending : plan.Decision,
                SubtotalJpy = plan.Estimate.SubtotalJpy,
                DiscountJpy = plan.Estimate.DiscountJpy,
                TotalJpy = plan.Estimate.TotalJpy,
                FinalizesAt = invoice ? now + FinalizeDelay(_options.Value.FinalizeAfterDays) : null,
                CreatedAt = now,
                UpdatedAt = now,
                Lines = BuildLines(plan.Estimate),
            };

            _context.BillingInvoices.Add(row);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // UNIQUE(year_month, account_subject) の衝突なら相手に任せる。行が無いなら別の失敗なので投げる。
                _context.Entry(row).State = EntityState.Detached;
                foreach (var line in row.Lines)
                {
                    _context.Entry(line).State = EntityState.Detached;
                }
                var exists = await _context.BillingInvoices
                    .AnyAsync(i => i.YearMonth == month.Value && i.AccountSubject == account.Subject, cancellationToken);
                if (exists)
                {
                    _logger.LogInformation(
                        "別の実行が先に月次請求の行を確保しました: Month={Month}, Account={Subject}", month, account.Subject);
                    return null;
                }
                throw;
            }

            _logger.LogInformation(
                "月次請求の行を確保しました: Month={Month}, Account={Subject}, Status={Status}, Total={Total}",
                month, account.Subject, row.Status, row.TotalJpy);
            return row;
        }

        /// <summary>
        /// Stripe に下書きの Invoice を作り、明細を積み、最後に自動確定を予約して draft にする。途中で落ちても、作成済みの
        /// ID を行に残しながら進めるので、次の実行は続きから再開する（同じ冪等キー・同じ金額で送る）。
        /// 確定の予約は全明細を積んだ後にだけ入れる（作成時に予約すると、再開が遅れたときに明細の欠けた請求書が
        /// 確定して請求され、確定後は明細を足せない）。
        /// </summary>
        private async Task IssueAsync(BillingInvoice row, UsageMonth month, CancellationToken cancellationToken)
        {
            var customerId = row.StripeCustomerId
                ?? throw new InvalidOperationException($"billing_invoice(id={row.Id}) に Stripe Customer がありません。");
            var finalizesAt = row.FinalizesAt
                ?? throw new InvalidOperationException($"billing_invoice(id={row.Id}) に確定日時がありません。");

            if (row.StripeInvoiceId == null)
            {
                var snapshot = await _stripe.CreateInvoiceAsync(
                    row.TenantName,
                    new InvoiceDraftRequest(
                        customerId,
                        $"EcAuth ご利用料金（{month.Year} 年 {month.Month} 月分）",
                        Metadata(row)),
                    idempotencyKey: $"ecauth-billing-invoice-{row.Id}",
                    cancellationToken);
                row.StripeInvoiceId = snapshot.Id;
                row.UpdatedAt = _time.GetUtcNow();
                await _context.SaveChangesAsync(cancellationToken);
            }

            foreach (var line in row.Lines.Where(l => l.AmountJpy != 0 && l.StripeInvoiceItemId == null).OrderBy(l => l.Position))
            {
                var metadata = new Dictionary<string, string>(Metadata(row))
                {
                    ["ecauth_billing_invoice_line_id"] = line.Id.ToString(CultureInfo.InvariantCulture),
                };
                if (line.ClientId != null)
                {
                    metadata["ecauth_client_id"] = line.ClientId;
                }
                line.StripeInvoiceItemId = await _stripe.AddInvoiceItemAsync(
                    row.TenantName,
                    customerId,
                    row.StripeInvoiceId,
                    line.AmountJpy,
                    line.Description,
                    metadata,
                    idempotencyKey: $"ecauth-billing-invoice-line-{line.Id}",
                    cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            // 再開が起票から大きく遅れて予定時刻を過ぎていたら、Stripe は過去の時刻を受け付けないので先送りする。
            var earliest = _time.GetUtcNow() + MinimumFinalizeDelay;
            if (finalizesAt < earliest)
            {
                _logger.LogWarning(
                    "月次請求の確定予定を過ぎていたため先送りします: Month={Month}, Account={Subject}, Planned={Planned}, Actual={Actual}",
                    month, row.AccountSubject, finalizesAt, earliest);
                finalizesAt = earliest;
                row.FinalizesAt = finalizesAt;
            }
            await _stripe.ScheduleFinalizationAsync(row.TenantName, row.StripeInvoiceId, finalizesAt, cancellationToken);

            row.Status = IMonthlyInvoicingService.Statuses.Draft;
            row.Error = null;
            row.UpdatedAt = _time.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "月次請求を起票しました: Month={Month}, Account={Subject}, Invoice={InvoiceId}, Total={Total}, FinalizesAt={FinalizesAt}",
                month, row.AccountSubject, row.StripeInvoiceId, row.TotalJpy, finalizesAt);
        }

        private async Task RecordErrorAsync(UsageMonth month, string accountSubject, Exception ex, CancellationToken cancellationToken)
        {
            // 失敗後の ChangeTracker は書きかけの状態を持ちうるので、捨ててから条件付きで書く。
            _context.ChangeTracker.Clear();
            var message = ex.Message.Length > MaxErrorLength ? ex.Message[..MaxErrorLength] : ex.Message;
            try
            {
                await _context.BillingInvoices
                    .Where(i => i.YearMonth == month.Value && i.AccountSubject == accountSubject
                        && i.Status == IMonthlyInvoicingService.Statuses.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Error, message)
                        .SetProperty(i => i.UpdatedAt, _time.GetUtcNow()), cancellationToken);
            }
            catch (Exception recordEx) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(recordEx, "月次請求の失敗を記録できませんでした: Month={Month}, Account={Subject}", month, accountSubject);
            }
        }

        private static List<BillingInvoiceLine> BuildLines(IBillingService.Estimate estimate)
        {
            var lines = new List<BillingInvoiceLine>();
            var position = 0;
            foreach (var org in estimate.Organizations)
            {
                foreach (var client in org.Clients)
                {
                    lines.Add(new BillingInvoiceLine
                    {
                        Kind = BillingInvoiceLine.KindClient,
                        Position = position++,
                        OrganizationId = org.OrganizationId,
                        OrganizationCode = org.Code,
                        OrganizationName = org.Name,
                        ClientDbId = client.Id,
                        ClientId = client.ClientId,
                        AppName = client.AppName,
                        SubjectType = client.SubjectType,
                        MonthlyActiveUsers = client.MonthlyActiveUsers,
                        FreeTierMau = client.FreeTierMau,
                        BillableUnits = client.BillableUnits,
                        ListPriceJpy = client.ListPriceJpy,
                        AmountJpy = client.AmountJpy,
                        ExemptReason = client.ExemptReason,
                        Description = Truncate(ClientDescription(estimate.Month, org, client)),
                    });
                }
            }

            if (estimate.DiscountJpy > 0)
            {
                lines.Add(new BillingInvoiceLine
                {
                    Kind = BillingInvoiceLine.KindDiscount,
                    Position = position,
                    ListPriceJpy = -estimate.DiscountJpy,
                    AmountJpy = -estimate.DiscountJpy,
                    Description = estimate.Plan.DiscountPercent is { } percent
                        ? string.Create(CultureInfo.InvariantCulture, $"割引（{percent}%）")
                        : "割引",
                });
            }
            return lines;
        }

        private static string ClientDescription(
            UsageMonth month, IBillingService.OrganizationEstimate org, IBillingService.ClientEstimate client)
        {
            var kind = client.SubjectType == SubjectType.B2B ? "B2B パスキー" : "B2C";
            return string.Create(CultureInfo.InvariantCulture,
                $"{org.Name} / {client.AppName}（{kind}）{month.Year} 年 {month.Month} 月 MAU {client.MonthlyActiveUsers}（無料枠 {client.FreeTierMau}）");
        }

        private static IReadOnlyDictionary<string, string> Metadata(BillingInvoice row) => new Dictionary<string, string>
        {
            ["ecauth_billing_invoice_id"] = row.Id.ToString(CultureInfo.InvariantCulture),
            ["ecauth_account_subject"] = row.AccountSubject,
            ["ecauth_year_month"] = row.YearMonth,
            ["ecauth_tenant"] = row.TenantName,
        };

        /// <summary>
        /// 起票から自動確定までの時間。0 以下（即時確定）でも Stripe は未来の時刻を要求するので 1 時間置く
        /// （Stripe の既定の自動確定と同じ間隔）。
        /// </summary>
        private static TimeSpan FinalizeDelay(int days) => days > 0 ? TimeSpan.FromDays(days) : MinimumFinalizeDelay;

        private static readonly TimeSpan MinimumFinalizeDelay = TimeSpan.FromHours(1);

        private static string Truncate(string value) =>
            value.Length > MaxDescriptionLength ? value[..MaxDescriptionLength] : value;
    }
}
