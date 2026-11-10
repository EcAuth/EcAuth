using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Test.TestHelpers
{
    /// <summary>
    /// 旧ユーザーのプール確定（条件付き UPDATE）を InMemory でも動く逐次 read-check-update に差し替えた
    /// テスト用サービス。本番（<see cref="UserService"/>）は <c>ExecuteUpdate</c> を使うが、EF Core の
    /// InMemory プロバイダーは非対応のため、ユニットテストでは単発の契約だけを検証する。
    /// 並行時の挙動は <c>UserServiceRelationalTests</c>（SQLite）で検証する。
    /// </summary>
    public sealed class TestableUserService : UserService
    {
        private readonly EcAuthDbContext _ctx;

        public TestableUserService(EcAuthDbContext ctx, ILogger<UserService> logger) : base(ctx, logger)
        {
            _ctx = ctx;
        }

        protected override async Task<int> TryClaimClientAsync(int userId, string clientId)
        {
            var user = await _ctx.EcAuthUsers.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId && u.ClientId == null);
            if (user == null)
            {
                return 0;
            }
            user.ClientId = clientId;
            await _ctx.SaveChangesAsync();
            return 1;
        }
    }
}
