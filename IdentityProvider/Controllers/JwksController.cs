using System.Security.Cryptography;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace IdentityProvider.Controllers
{
    /// <summary>
    /// JWKS (JSON Web Key Set) endpoint。
    /// OpenID Connect Discovery の jwks_uri から参照される公開鍵セットを返します。
    /// host ルート直下の固定パス（.well-known/jwks.json）で公開し、/v1/ 配下には置きません。
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    public class JwksController : ControllerBase
    {
        private readonly EcAuthDbContext _context;
        private readonly ITenantService _tenantService;
        private readonly ILogger<JwksController> _logger;

        public JwksController(
            EcAuthDbContext context,
            ITenantService tenantService,
            ILogger<JwksController> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _logger = logger;
        }

        /// <summary>
        /// 現在の Organization の有効な RSA 公開鍵を JWKS 形式で返します。
        /// </summary>
        /// <returns>{ "keys": [ ... ] } 形式の JSON Web Key Set</returns>
        [HttpGet(".well-known/jwks.json")]
        public async Task<IActionResult> Get()
        {
            // Organization の特定は Organizations への global query filter
            // （o.TenantName == _tenantService.TenantName、TenantMiddleware が設定）に委ねる。
            var organization = await _context.Organizations.FirstOrDefaultAsync();
            if (organization == null)
            {
                _logger.LogWarning("JWKS: Organization not found for tenant {TenantName}", _tenantService.TenantName);
                // 該当 Organization が無い場合も 500 にせず空の keys を返す。
                return Ok(new { keys = Array.Empty<object>() });
            }

            // RsaKeyPair には query filter が無いため明示的に OrganizationId で絞り込む
            // （TokenService の鍵取得と同じパターン）。当面は IsActive==true の鍵のみ返す。
            var rsaKeyPairs = await _context.RsaKeyPairs
                .IgnoreQueryFilters()
                .Where(k => k.OrganizationId == organization.Id && k.IsActive)
                .ToListAsync();

            var keys = new List<object>();
            foreach (var rsaKeyPair in rsaKeyPairs)
            {
                try
                {
                    using var rsa = RSA.Create();
                    // PublicKey は Base64 エンコードされた DER(PKCS#1) 形式で格納されている
                    // （生成箇所: OrganizationClientSeeder の rsa.ExportRSAPublicKey()。
                    //  TokenService の検証時も ImportRSAPublicKey を使用）。
                    rsa.ImportRSAPublicKey(Convert.FromBase64String(rsaKeyPair.PublicKey), out _);

                    var parameters = rsa.ExportParameters(false);

                    keys.Add(new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = "RS256",
                        kid = rsaKeyPair.Kid,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent),
                    });
                }
                catch (Exception ex)
                {
                    // 1 鍵の変換失敗で endpoint 全体を落とさず、当該鍵のみスキップする。
                    _logger.LogWarning(ex, "JWKS: Failed to convert RSA public key (Kid={Kid})", rsaKeyPair.Kid);
                }
            }

            return Ok(new { keys });
        }
    }
}
