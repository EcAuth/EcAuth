using IdentityProvider.Models;
using IdentityProvider.Test.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IdentityProvider.Test.Models
{
    /// <summary>
    /// <see cref="ClientQueryExtensions.ExcludeDeletedOrganizations"/> が、Organization の
    /// グローバルクエリフィルターと組み合わさったときにどう振る舞うかをリレーショナルな SQLite で確かめる。
    /// </summary>
    public sealed class ClientQueryExtensionsRelationalTests : IDisposable
    {
        private readonly RetryingSqliteContext _db = new();

        public ClientQueryExtensionsRelationalTests()
        {
            _db.Context.Organizations.Add(new Organization
            {
                Id = 1,
                Code = "deleted-org",
                Name = "Deleted Org",
                TenantName = "test-tenant",
                DeletedAt = DateTimeOffset.UtcNow
            });
            _db.Context.Clients.Add(new Client { Id = 1, ClientId = "deleted-client", ClientSecret = "s", AppName = "A", OrganizationId = 1 });
            _db.Context.SaveChanges();
            _db.Context.ChangeTracker.Clear();
        }

        [Fact]
        public async Task WithIgnoreQueryFilters_ExcludesClientOfDeletedOrganization()
        {
            var client = await _db.Context.Clients
                .IgnoreQueryFilters()
                .ExcludeDeletedOrganizations()
                .FirstOrDefaultAsync(c => c.ClientId == "deleted-client");

            Assert.Null(client);
        }

        [Fact]
        public async Task WithoutIgnoreQueryFilters_DoesNotExcludeClientOfDeletedOrganization()
        {
            // 今の挙動の固定。Organization のクエリフィルター（DeletedAt == null）が先に効いて
            // ナビゲーションが null になり、「Organization を持たない Client」として素通りする。
            // /v1/authorization・/v1/auth/callback・/v1/token の Client 検索がこの形。
            // EcAuthDocs#164 段 4（F-1: Client への HasQueryFilter）で直すときに期待値を反転させる。
            var client = await _db.Context.Clients
                .ExcludeDeletedOrganizations()
                .FirstOrDefaultAsync(c => c.ClientId == "deleted-client");

            Assert.NotNull(client);
        }

        public void Dispose() => _db.Dispose();
    }
}
