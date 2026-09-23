using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// <see cref="MonthlyActiveUserRecorder"/> の記録（EcAuthDocs#45）。
    ///
    /// 記録は生 SQL の <c>INSERT ... WHERE NOT EXISTS</c> で、重複排除の最終防衛線は UNIQUE index。
    /// InMemory プロバイダーは生 SQL を実行できず UNIQUE も強制しないため、
    /// <see cref="RetryingSqliteContext"/>（SQLite + EnsureCreated）で実 DB 相当に検証する。
    /// </summary>
    public class MonthlyActiveUserRecorderTests : IDisposable
    {
        private readonly RetryingSqliteContext _db;
        private readonly Mock<ILogger<MonthlyActiveUserRecorder>> _logger = new();
        private readonly MonthlyActiveUserRecorder _recorder;

        private readonly Organization _organization;
        private readonly Client _client;
        private readonly Client _otherClient;
        private readonly B2BUser _user;

        private static readonly DateTimeOffset August = new(2026, 8, 10, 3, 0, 0, TimeSpan.Zero);

        private EcAuthDbContext Context => _db.Context;

        public MonthlyActiveUserRecorderTests()
        {
            _db = new RetryingSqliteContext();
            _recorder = new MonthlyActiveUserRecorder(Context, _logger.Object);

            _organization = new Organization { Id = 1, Code = "shop-a", Name = "Shop A", TenantName = "shop-a" };
            _client = new Client { Id = 10, ClientId = "client-a", ClientSecret = "s", AppName = "A", OrganizationId = 1, SubjectType = SubjectType.B2B };
            _otherClient = new Client { Id = 11, ClientId = "client-a-wp", ClientSecret = "s", AppName = "A WP", OrganizationId = 1, SubjectType = SubjectType.B2B };
            _user = new B2BUser { Subject = "11111111-1111-1111-1111-111111111111", OrganizationId = 1 };
            Context.Organizations.Add(_organization);
            Context.Clients.AddRange(_client, _otherClient);
            Context.B2BUsers.Add(_user);
            Context.SaveChanges();
        }

        public void Dispose() => _db.Dispose();

        private ITokenService.TokenRequest B2BRequest(Client? client = null, GrantType grantType = GrantType.AuthorizationCode, string? authMethod = null)
        {
            return new ITokenService.TokenRequest
            {
                User = _user,
                Client = client ?? _client,
                SubjectType = SubjectType.B2B,
                GrantType = grantType,
                AuthMethod = authMethod
            };
        }

        private Task<List<MonthlyActiveUser>> RowsAsync() => Context.MonthlyActiveUsers.OrderBy(m => m.Id).ToListAsync();

        [Fact]
        public async Task RecordAsync_FirstLoginInMonth_InsertsOneRow()
        {
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, August, CancellationToken.None);

            var row = Assert.Single(await RowsAsync());
            Assert.Equal("2026-08", row.YearMonth);
            Assert.Equal(_client.Id, row.ClientId);
            Assert.Equal(_organization.Id, row.OrganizationId);
            Assert.Equal(_user.Subject, row.Subject);
            Assert.Equal(SubjectType.B2B, row.SubjectType);
            Assert.Equal(MonthlyActiveUserRecorder.AuthMethodB2BPasskey, row.AuthMethod);
            Assert.Equal(August, row.FirstSeenAt);
        }

        [Fact]
        public async Task RecordAsync_SecondLoginInSameMonth_DoesNotInsertOrUpdate()
        {
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, August, CancellationToken.None);
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, August.AddDays(5), CancellationToken.None);

            var row = Assert.Single(await RowsAsync());
            // write-once: first_seen_at は最初のログインのまま（last_seen_at は持たない）
            Assert.Equal(August, row.FirstSeenAt);
        }

        [Fact]
        public async Task RecordAsync_DifferentClientOrMonth_InsertsSeparateRows()
        {
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, August, CancellationToken.None);
            await _recorder.RecordAsync(B2BRequest(_otherClient), _user.Subject, August, CancellationToken.None);
            // 8/31 15:00Z は JST では 9/1
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, new DateTimeOffset(2026, 8, 31, 15, 0, 0, TimeSpan.Zero), CancellationToken.None);

            var rows = await RowsAsync();
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { ("2026-08", _client.Id), ("2026-08", _otherClient.Id), ("2026-09", _client.Id) },
                rows.Select(r => (r.YearMonth, r.ClientId)).ToArray());
        }

        [Fact]
        public async Task RecordAsync_AccountSubject_IsNotRecorded()
        {
            var request = new ITokenService.TokenRequest
            {
                User = _user,
                Client = _client,
                SubjectType = SubjectType.Account,
                GrantType = GrantType.AuthorizationCode
            };

            await _recorder.RecordAsync(request, _user.Subject, August, CancellationToken.None);

            Assert.Empty(await RowsAsync());
        }

        [Theory]
        [InlineData(GrantType.RefreshToken)]
        [InlineData(GrantType.MagicLink)]
        public async Task RecordAsync_NonAuthorizationCodeGrant_IsNotRecorded(GrantType grantType)
        {
            await _recorder.RecordAsync(B2BRequest(grantType: grantType), _user.Subject, August, CancellationToken.None);

            Assert.Empty(await RowsAsync());
        }

        [Fact]
        public async Task RecordAsync_UsesExplicitAuthMethodOverInference()
        {
            await _recorder.RecordAsync(B2BRequest(authMethod: "b2b_sso"), _user.Subject, August, CancellationToken.None);

            Assert.Equal("b2b_sso", Assert.Single(await RowsAsync()).AuthMethod);
        }

        [Fact]
        public async Task RecordAsync_B2CSubject_InfersSocialAuthMethod()
        {
            var b2cClient = new Client { Id = 20, ClientId = "client-b2c", ClientSecret = "s", AppName = "B2C", OrganizationId = 1, SubjectType = SubjectType.B2C };
            var b2cUser = new EcAuthUser { Subject = "b2c-subject", EmailHash = "hash", OrganizationId = 1 };
            Context.Clients.Add(b2cClient);
            Context.EcAuthUsers.Add(b2cUser);
            await Context.SaveChangesAsync();
            var request = new ITokenService.TokenRequest
            {
                User = b2cUser,
                Client = b2cClient,
                SubjectType = SubjectType.B2C,
                GrantType = GrantType.AuthorizationCode
            };

            await _recorder.RecordAsync(request, b2cUser.Subject, August, CancellationToken.None);

            var row = Assert.Single(await RowsAsync());
            Assert.Equal(SubjectType.B2C, row.SubjectType);
            Assert.Equal(MonthlyActiveUserRecorder.AuthMethodB2CSocial, row.AuthMethod);
        }

        [Fact]
        public async Task RecordAsync_DatabaseFailure_DoesNotThrowAndLogsError()
        {
            // 契約: 集計の失敗で認証を落とさない。テーブルを落として INSERT を確実に失敗させる。
            await Context.Database.ExecuteSqlRawAsync("DROP TABLE monthly_active_user");

            var ex = await Record.ExceptionAsync(() => _recorder.RecordAsync(B2BRequest(), _user.Subject, August, CancellationToken.None));

            Assert.Null(ex);
            _logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to record MAU")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        [Fact]
        public async Task RecordAsync_ClientWithoutOrganization_DoesNotThrow()
        {
            var orphan = new Client { Id = 30, ClientId = "orphan", ClientSecret = "s", AppName = "Orphan", OrganizationId = null };

            var ex = await Record.ExceptionAsync(() => _recorder.RecordAsync(B2BRequest(orphan), _user.Subject, August, CancellationToken.None));

            Assert.Null(ex);
            Assert.Empty(await RowsAsync());
        }

        [Fact]
        public async Task UniqueIndex_RejectsDuplicateRowsInsertedOutsideRecorder()
        {
            // recorder をすり抜けた重複（レース）を UNIQUE index が最終的に弾くことを、実 DB（SQLite）で確認する
            await _recorder.RecordAsync(B2BRequest(), _user.Subject, August, CancellationToken.None);
            Context.MonthlyActiveUsers.Add(new MonthlyActiveUser
            {
                YearMonth = "2026-08",
                ClientId = _client.Id,
                OrganizationId = _organization.Id,
                Subject = _user.Subject,
                SubjectType = SubjectType.B2B,
                AuthMethod = "b2b_passkey",
                FirstSeenAt = August
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => Context.SaveChangesAsync());
        }
    }
}
