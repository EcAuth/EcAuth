using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace IdentityProvider.Test.TestHelpers
{
    public static class TestDbContextHelper
    {
        /// <summary>
        /// 指定した scheme / host を返す IIssuerResolver を生成するテストヘルパー。
        /// TokenService は IIssuerResolver 経由で issuer（"{scheme}://{host}"）を取得するため、
        /// 既存テストの "https://test.ec-cube.io" 期待値を維持したまま注入できるようにする。
        /// </summary>
        public static IIssuerResolver CreateIssuerResolver(string scheme = "https", string host = "test.ec-cube.io")
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = scheme;
            httpContext.Request.Host = new HostString(host);
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            return new IssuerResolver(httpContextAccessor);
        }

        /// <summary>
        /// テスト用の <see cref="TokenService"/> を生成する。ctor の引数が増えたとき（EcAuthDocs#45 で
        /// <see cref="IMonthlyActiveUserRecorder"/> を追加）に 30 箇所の <c>new TokenService(...)</c> を
        /// 追いかけなくて済むよう、生成をここに集約する。
        /// <paramref name="recorder"/> 省略時は何もしない <see cref="NoOpMonthlyActiveUserRecorder"/>。
        /// InMemory プロバイダーでは生 SQL を実行できないため、本物の recorder を InMemory の context と
        /// 組み合わせないこと（記録の検証は <see cref="RetryingSqliteContext"/> で行う）。
        /// </summary>
        public static TokenService CreateTokenService(
            EcAuthDbContext context,
            ILogger<TokenService> logger,
            IIssuerResolver issuerResolver,
            IMonthlyActiveUserRecorder? recorder = null)
        {
            return new TokenService(context, logger, issuerResolver, recorder ?? new NoOpMonthlyActiveUserRecorder());
        }

        public static EcAuthDbContext CreateInMemoryContext(string? databaseName = null, ITenantService? tenantService = null)
        {
            var dbName = databaseName ?? Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<EcAuthDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                // InMemory プロバイダーはトランザクション非対応のため、
                // BeginTransaction を呼ぶサービス（SignupService 等）のテストで例外化される
                // TransactionIgnoredWarning を無視する。
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            var mockTenantService = tenantService ?? new MockTenantService();
            return new EcAuthDbContext(options, mockTenantService);
        }

        public static RsaKeyPair GenerateAndAddRsaKeyPair(EcAuthDbContext context, Organization organization, int keyId)
        {
            string publicKey, privateKey;
            using (var rsa = RSA.Create(2048))
            {
                publicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey());
                privateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
            }

            var rsaKeyPair = new RsaKeyPair
            {
                Id = keyId,
                Kid = Guid.NewGuid().ToString(),
                OrganizationId = organization.Id,
                PublicKey = publicKey,
                PrivateKey = privateKey,
                Organization = organization
            };
            context.RsaKeyPairs.Add(rsaKeyPair);
            return rsaKeyPair;
        }
    }

    /// <summary>
    /// MAU を記録しない <see cref="IMonthlyActiveUserRecorder"/>。トークン発行自体を検証するテスト用。
    /// </summary>
    public sealed class NoOpMonthlyActiveUserRecorder : IMonthlyActiveUserRecorder
    {
        public Task RecordAsync(ITokenService.TokenRequest request, string subject, DateTimeOffset issuedAt, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class MockTenantService : ITenantService
    {
        public string TenantName { get; private set; } = "test-tenant";

        public void SetTenant(string tenantName)
        {
            TenantName = tenantName;
        }
    }
}