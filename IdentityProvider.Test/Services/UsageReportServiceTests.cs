using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="UsageReportService"/> の集計（EcAuthDocs#45）。
    ///
    /// 呼び出し元（accounts テナントのマイページ API / ConsoleApp）から見て顧客 Organization は
    /// 別テナントなので、テナントを <c>accounts</c> に切り替えた状態で数字が返ることを最重要の検証とする
    /// （<c>IgnoreQueryFilters</c> 漏れの検出）。GroupBy / COUNT(DISTINCT) が SQL に翻訳できることも
    /// 兼ねて、リレーショナルな SQLite（<see cref="RetryingSqliteContext"/>）で実行する。
    /// </summary>
    public class UsageReportServiceTests : IDisposable
    {
        private readonly MockTenantService _tenant = new();
        private readonly RetryingSqliteContext _db;
        private readonly UsageReportService _service;

        private static readonly DateTimeOffset August = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        private static readonly UsageMonth AugustMonth = UsageMonth.FromInstant(August);

        private EcAuthDbContext Context => _db.Context;

        public UsageReportServiceTests()
        {
            _tenant.SetTenant("shop-a");
            _db = new RetryingSqliteContext(_tenant);
            _service = new UsageReportService(Context, new B2BUserService(Context, Mock.Of<ILogger<B2BUserService>>()));
        }

        public void Dispose() => _db.Dispose();

        private Organization SeedOrganization(int id, string code, bool isSandbox = false, DateTimeOffset? deletedAt = null)
        {
            var org = new Organization { Id = id, Code = code, Name = $"Org {code}", TenantName = code, IsSandbox = isSandbox, DeletedAt = deletedAt };
            Context.Organizations.Add(org);
            Context.SaveChanges();
            return org;
        }

        private Client SeedClient(int id, int orgId, string clientId, SubjectType subjectType = SubjectType.B2B)
        {
            var client = new Client { Id = id, ClientId = clientId, ClientSecret = "s", AppName = $"App {clientId}", OrganizationId = orgId, SubjectType = subjectType };
            Context.Clients.Add(client);
            Context.SaveChanges();
            return client;
        }

        private void SeedMau(string yearMonth, Client client, string subject)
        {
            Context.MonthlyActiveUsers.Add(new MonthlyActiveUser
            {
                YearMonth = yearMonth,
                ClientId = client.Id,
                OrganizationId = client.OrganizationId!.Value,
                Subject = subject,
                SubjectType = SubjectType.B2B,
                AuthMethod = "b2b_passkey",
                FirstSeenAt = August
            });
            Context.SaveChanges();
        }

        private void SeedRegisteredUser(int orgId, Client client, string subject)
        {
            if (!Context.B2BUsers.Any(u => u.Subject == subject))
            {
                Context.B2BUsers.Add(new B2BUser { Subject = subject, OrganizationId = orgId });
            }
            Context.B2BUserIdentities.Add(new B2BUserIdentity
            {
                B2BSubject = subject,
                IssuerKey = $"client:{client.ClientId}",
                ExternalId = $"{client.ClientId}:{subject}",
                ClientId = client.ClientId
            });
            Context.SaveChanges();
        }

        private Task<IUsageReportService.UsageReport> ReportAsync(bool includeNonBillable = true, params int[] orgIds)
        {
            return _service.GetReportAsync(new IUsageReportService.UsageReportQuery(AugustMonth, orgIds, includeNonBillable));
        }

        [Fact]
        public async Task GetReportAsync_ReturnsFiguresAcrossTenants()
        {
            var org = SeedOrganization(1, "shop-a");
            var client = SeedClient(10, org.Id, "client-a");
            SeedMau("2026-08", client, "u1");
            SeedRegisteredUser(org.Id, client, "u1");

            // 呼び出し元は accounts テナント。フィルター越しには Organization も B2BUser も見えない
            _tenant.SetTenant("accounts");
            Assert.Empty(Context.Organizations.ToList());

            var report = await ReportAsync(orgIds: org.Id);

            var o = Assert.Single(report.Organizations);
            Assert.Equal("shop-a", o.Code);
            Assert.Equal(1, o.MonthlyActiveUsers);
            var c = Assert.Single(o.Clients);
            Assert.Equal("client-a", c.ClientId);
            Assert.Equal(1, c.MonthlyActiveUsers);
            Assert.Equal(1, c.RegisteredB2BUsers);
            Assert.Equal(AugustMonth, report.Month);
            Assert.InRange(report.AsOf, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        }

        [Fact]
        public async Task GetReportAsync_ClientMauIsBillingValue_OrgDistinctIsReference()
        {
            // 同一人物（u1）が 2 Client で認証: client 別は 2 + 1、org distinct は 2（u1, u2）
            var org = SeedOrganization(1, "shop-a");
            var eccube = SeedClient(10, org.Id, "client-a");
            var wordpress = SeedClient(11, org.Id, "client-a-wp");
            SeedMau("2026-08", eccube, "u1");
            SeedMau("2026-08", eccube, "u2");
            SeedMau("2026-08", wordpress, "u1");
            // 別月・別 Organization は混ざらない
            SeedMau("2026-07", eccube, "u3");
            var other = SeedOrganization(2, "shop-b");
            var otherClient = SeedClient(20, other.Id, "client-b");
            SeedMau("2026-08", otherClient, "u1");

            var report = await ReportAsync(orgIds: org.Id);

            var o = Assert.Single(report.Organizations);
            Assert.Equal(2, o.MonthlyActiveUsers);
            Assert.Equal(3, o.Clients.Sum(c => c.MonthlyActiveUsers));
            Assert.Equal(2, o.Clients.Single(c => c.ClientId == "client-a").MonthlyActiveUsers);
            Assert.Equal(1, o.Clients.Single(c => c.ClientId == "client-a-wp").MonthlyActiveUsers);
        }

        [Fact]
        public async Task GetReportAsync_IncludesClientsWithZeroMau_ExcludesAccountClients()
        {
            var org = SeedOrganization(1, "shop-a");
            SeedClient(10, org.Id, "client-a");
            SeedClient(11, org.Id, "client-console", SubjectType.Account);

            var report = await ReportAsync(orgIds: org.Id);

            var o = Assert.Single(report.Organizations);
            Assert.Equal(0, o.MonthlyActiveUsers);
            var c = Assert.Single(o.Clients);
            Assert.Equal("client-a", c.ClientId);
            Assert.Equal(0, c.MonthlyActiveUsers);
            Assert.Equal(0, c.RegisteredB2BUsers);
        }

        [Fact]
        public async Task GetReportAsync_OnlyRequestedOrganizations()
        {
            var org = SeedOrganization(1, "shop-a");
            var other = SeedOrganization(2, "shop-b");
            SeedClient(10, org.Id, "client-a");
            SeedClient(20, other.Id, "client-b");

            var report = await ReportAsync(orgIds: org.Id);

            Assert.Equal(new[] { 1 }, report.Organizations.Select(o => o.OrganizationId));
        }

        [Fact]
        public async Task GetReportAsync_EmptyOrganizationIds_ReturnsEmptyReport()
        {
            SeedOrganization(1, "shop-a");

            var report = await ReportAsync();

            Assert.Empty(report.Organizations);
            Assert.Equal(AugustMonth, report.Month);
        }

        [Fact]
        public async Task GetReportAsync_NonBillablePolicy_AppliedAtAggregationTime()
        {
            var production = SeedOrganization(1, "shop-a");
            var sandbox = SeedOrganization(2, "shop-a-sandbox", isSandbox: true);
            var staging = SeedOrganization(3, "stg-shop-a");
            var deleted = SeedOrganization(4, "shop-old", deletedAt: August);
            foreach (var org in new[] { production, sandbox, staging, deleted })
            {
                var client = SeedClient(org.Id * 10, org.Id, $"client-{org.Code}");
                SeedMau("2026-08", client, "u1");
            }
            var allIds = new[] { 1, 2, 3, 4 };

            // マイページ向け: 全部返し、is_billable で区別する
            var full = await ReportAsync(includeNonBillable: true, allIds);
            Assert.Equal(4, full.Organizations.Count);
            Assert.True(full.Organizations.Single(o => o.OrganizationId == 1).IsBillable);
            Assert.False(full.Organizations.Single(o => o.OrganizationId == 2).IsBillable);
            Assert.False(full.Organizations.Single(o => o.OrganizationId == 3).IsBillable);
            Assert.False(full.Organizations.Single(o => o.OrganizationId == 4).IsBillable);
            Assert.True(full.Organizations.Single(o => o.OrganizationId == 2).IsSandbox);

            // 請求向け: 請求対象だけ
            var billable = await ReportAsync(includeNonBillable: false, allIds);
            var only = Assert.Single(billable.Organizations);
            Assert.Equal(1, only.OrganizationId);
            Assert.Equal(1, only.Clients.Single().MonthlyActiveUsers);
        }

        [Fact]
        public async Task GetReportAsync_RegisteredUsersCountedPerClientId()
        {
            var org = SeedOrganization(1, "shop-a");
            var eccube = SeedClient(10, org.Id, "client-a");
            var wordpress = SeedClient(11, org.Id, "client-a-wp");
            SeedRegisteredUser(org.Id, eccube, "u1");
            SeedRegisteredUser(org.Id, eccube, "u2");
            SeedRegisteredUser(org.Id, wordpress, "u1");
            // 登録ユーザーが物理削除されても MAU 行は残るので MAU > 登録数は正常
            SeedMau("2026-08", eccube, "u1");
            SeedMau("2026-08", eccube, "u2");
            SeedMau("2026-08", eccube, "u-deleted");

            var report = await ReportAsync(orgIds: org.Id);

            var o = Assert.Single(report.Organizations);
            var a = o.Clients.Single(c => c.ClientId == "client-a");
            Assert.Equal(2, a.RegisteredB2BUsers);
            Assert.Equal(3, a.MonthlyActiveUsers);
            Assert.Equal(1, o.Clients.Single(c => c.ClientId == "client-a-wp").RegisteredB2BUsers);
        }
    }
}
