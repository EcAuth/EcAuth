using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace IdentityProvider.Test.TestHelpers
{
    public static class TestDbContextHelper
    {
        public static EcAuthDbContext CreateInMemoryContext(string? databaseName = null, ITenantService? tenantService = null)
        {
            var dbName = databaseName ?? Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<EcAuthDbContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;

            var mockTenantService = tenantService ?? new MockTenantService();
            return new EcAuthDbContext(options, mockTenantService);
        }

        public static RsaKeyPair GenerateAndAddRsaKeyPair(EcAuthDbContext context, Client client, int keyId)
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
                ClientId = client.Id,
                PublicKey = publicKey,
                PrivateKey = privateKey,
                Client = client
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