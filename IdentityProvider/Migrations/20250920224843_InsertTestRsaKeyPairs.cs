using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class InsertTestRsaKeyPairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Generate a real RSA key pair for testing (2048-bit)
            using var rsa = RSA.Create(2048);

            // Export as raw bytes and convert to Base64 (as expected by TokenService)
            var publicKeyBytes = rsa.ExportRSAPublicKey();
            var privateKeyBytes = rsa.ExportRSAPrivateKey();

            var publicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
            var privateKeyBase64 = Convert.ToBase64String(privateKeyBytes);

            migrationBuilder.Sql($@"
                IF NOT EXISTS (SELECT 1 FROM dbo.rsa_key_pair WHERE client_id = 1)
                BEGIN
                    EXEC(N'INSERT INTO [dbo].[rsa_key_pair] ([client_id], [public_key], [private_key])
                    VALUES (1, N''{publicKeyBase64}'', N''{privateKeyBase64}'')')
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "rsa_key_pair",
                keyColumn: "client_id",
                keyValue: 1);
        }
    }
}
