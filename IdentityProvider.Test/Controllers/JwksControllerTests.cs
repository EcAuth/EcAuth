using System.Security.Cryptography;
using IdentityProvider.Controllers;
using IdentityProvider.Models;
using IdentityProvider.Services;
using IdentityProvider.Test.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace IdentityProvider.Test.Controllers
{
    public class JwksControllerTests
    {
        private readonly ILogger<JwksController> _logger;

        public JwksControllerTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<JwksController>();
        }

        /// <summary>
        /// 匿名オブジェクトのプロパティ値をリフレクションで取得する。
        /// JwksController は匿名型で JWK を構築するため、テスト側もリフレクションで検証する。
        /// </summary>
        private static object? GetProp(object obj, string name)
            => obj.GetType().GetProperty(name)?.GetValue(obj);

        /// <summary>
        /// Ok(new { keys }) の keys を List&lt;object&gt; として取り出す。
        /// </summary>
        private static List<object> GetKeys(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
            var keysObj = GetProp(ok.Value!, "keys");
            Assert.NotNull(keysObj);
            var keys = Assert.IsAssignableFrom<System.Collections.IEnumerable>(keysObj!);
            return keys.Cast<object>().ToList();
        }

        [Fact]
        public async Task Get_ActiveRsaKeyPair_ReturnsJwkWithCorrectFields()
        {
            // Arrange - MockTenantService の TenantName "test-tenant" に合わせた Organization を用意
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            // 公開鍵パラメータ（n/e）の期待値を保持するため、ここで鍵を生成する
            RSAParameters expected;
            string publicKeyDer, kid;
            using (var rsa = RSA.Create(2048))
            {
                expected = rsa.ExportParameters(false);
                publicKeyDer = Convert.ToBase64String(rsa.ExportRSAPublicKey());
            }
            kid = Guid.NewGuid().ToString();

            context.RsaKeyPairs.Add(new RsaKeyPair
            {
                Id = 1,
                Kid = kid,
                OrganizationId = organization.Id,
                PublicKey = publicKeyDer,
                PrivateKey = "unused-in-jwks",
                IsActive = true,
                Organization = organization
            });
            await context.SaveChangesAsync();

            var controller = new JwksController(context, new MockTenantService(), _logger);

            // Act
            var keys = GetKeys(await controller.Get());

            // Assert - active 鍵 1 件が JWK として返ること
            var jwk = Assert.Single(keys);
            Assert.Equal("RSA", GetProp(jwk, "kty"));
            Assert.Equal("sig", GetProp(jwk, "use"));
            Assert.Equal("RS256", GetProp(jwk, "alg"));
            Assert.Equal(kid, GetProp(jwk, "kid"));

            // n/e が元の公開鍵パラメータと一致すること（Base64Url エンコードして比較）
            var expectedN = Base64UrlEncoder.Encode(expected.Modulus);
            var expectedE = Base64UrlEncoder.Encode(expected.Exponent);
            Assert.Equal(expectedN, GetProp(jwk, "n"));
            Assert.Equal(expectedE, GetProp(jwk, "e"));

            // 逆方向検証: n/e から復元した公開鍵が元の鍵と一致すること
            using var restored = RSA.Create();
            restored.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlEncoder.DecodeBytes((string)GetProp(jwk, "n")!),
                Exponent = Base64UrlEncoder.DecodeBytes((string)GetProp(jwk, "e")!)
            });
            var restoredParams = restored.ExportParameters(false);
            Assert.Equal(expected.Modulus, restoredParams.Modulus);
            Assert.Equal(expected.Exponent, restoredParams.Exponent);
        }

        [Fact]
        public async Task Get_NoActiveKey_ReturnsEmptyKeys()
        {
            // Arrange - Organization はあるが active 鍵が無い（IsActive=false のみ）
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            string publicKeyDer;
            using (var rsa = RSA.Create(2048))
            {
                publicKeyDer = Convert.ToBase64String(rsa.ExportRSAPublicKey());
            }

            context.RsaKeyPairs.Add(new RsaKeyPair
            {
                Id = 1,
                Kid = Guid.NewGuid().ToString(),
                OrganizationId = organization.Id,
                PublicKey = publicKeyDer,
                PrivateKey = "unused",
                IsActive = false, // 無効化済み
                Organization = organization
            });
            await context.SaveChangesAsync();

            var controller = new JwksController(context, new MockTenantService(), _logger);

            // Act
            var keys = GetKeys(await controller.Get());

            // Assert - 空配列（500 にしない）
            Assert.Empty(keys);
        }

        [Fact]
        public async Task Get_OrganizationNotFound_ReturnsEmptyKeys()
        {
            // Arrange - Organization を一切登録しない
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var controller = new JwksController(context, new MockTenantService(), _logger);

            // Act
            var keys = GetKeys(await controller.Get());

            // Assert - 空配列を返し例外にしない
            Assert.Empty(keys);
        }

        [Fact]
        public async Task Get_MultipleActiveKeys_ReturnsAllAsJwk()
        {
            // Arrange - active 鍵を 2 件用意
            using var context = TestDbContextHelper.CreateInMemoryContext();
            var organization = new Organization { Id = 1, Code = "TESTORG", Name = "TestOrg", TenantName = "test-tenant" };
            context.Organizations.Add(organization);

            var kids = new List<string>();
            for (var i = 1; i <= 2; i++)
            {
                string publicKeyDer;
                using (var rsa = RSA.Create(2048))
                {
                    publicKeyDer = Convert.ToBase64String(rsa.ExportRSAPublicKey());
                }
                var kid = Guid.NewGuid().ToString();
                kids.Add(kid);
                context.RsaKeyPairs.Add(new RsaKeyPair
                {
                    Id = i,
                    Kid = kid,
                    OrganizationId = organization.Id,
                    PublicKey = publicKeyDer,
                    PrivateKey = "unused",
                    IsActive = true,
                    Organization = organization
                });
            }
            await context.SaveChangesAsync();

            var controller = new JwksController(context, new MockTenantService(), _logger);

            // Act
            var keys = GetKeys(await controller.Get());

            // Assert - 2 件とも返り、kid が一致すること
            Assert.Equal(2, keys.Count);
            var returnedKids = keys.Select(k => (string)GetProp(k, "kid")!).ToList();
            Assert.All(kids, kid => Assert.Contains(kid, returnedKids));
        }
    }
}
