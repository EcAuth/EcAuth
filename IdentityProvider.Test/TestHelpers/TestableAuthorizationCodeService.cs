using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Test.TestHelpers
{
    /// <summary>
    /// 使用済みマーキングの CAS シームを InMemory でも動く逐次 read-check-update に差し替えたテスト用サービス。
    /// 本番（<see cref="AuthorizationCodeService"/>）は <c>ExecuteUpdate</c> によるアトミック UPDATE を使うが、
    /// EF Core の InMemory プロバイダーは非対応のため、ユニットテストでは単発使用の契約のみ検証する。
    /// </summary>
    public sealed class TestableAuthorizationCodeService : AuthorizationCodeService
    {
        private readonly EcAuthDbContext _ctx;

        public TestableAuthorizationCodeService(EcAuthDbContext ctx, ILogger<AuthorizationCodeService> logger)
            : base(ctx, logger)
        {
            _ctx = ctx;
        }

        protected override async Task<int> TryMarkAsUsedAsync(string code, DateTimeOffset now)
        {
            var authorizationCode = await _ctx.AuthorizationCodes
                .FirstOrDefaultAsync(ac => ac.Code == code && !ac.IsUsed && ac.ExpiresAt > now);
            if (authorizationCode == null)
            {
                return 0;
            }
            authorizationCode.IsUsed = true;
            authorizationCode.UsedAt = now;
            await _ctx.SaveChangesAsync();
            return 1;
        }
    }
}
