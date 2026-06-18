using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    public class MockTenantService : ITenantService
    {
        public string TenantName { get; private set; } = "test-tenant";

        public void SetTenant(string tenantName)
        {
            TenantName = tenantName;
        }
    }
}