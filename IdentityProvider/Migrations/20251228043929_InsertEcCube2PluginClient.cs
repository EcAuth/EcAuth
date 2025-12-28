using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class InsertEcCube2PluginClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 環境変数から値を取得
            DotNetEnv.Env.TraversePath().Load();
            var STAGING_ORGANIZATION_CODE = DotNetEnv.Env.GetString("STAGING_ORGANIZATION_CODE");
            var ECCUBE2_CLIENT_ID = DotNetEnv.Env.GetString("ECCUBE2_CLIENT_ID");
            var ECCUBE2_CLIENT_SECRET = DotNetEnv.Env.GetString("ECCUBE2_CLIENT_SECRET");
            var ECCUBE2_REDIRECT_URI = DotNetEnv.Env.GetString("ECCUBE2_REDIRECT_URI");

            // MockIdP の設定（既存の staging と同じ設定を使用）
            var STAGING_MOCK_IDP_APP_NAME = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_APP_NAME");
            var STAGING_MOCK_IDP_CLIENT_ID = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_CLIENT_ID");
            var STAGING_MOCK_IDP_CLIENT_SECRET = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_CLIENT_SECRET");
            var STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT");
            var STAGING_MOCK_IDP_TOKEN_ENDPOINT = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_TOKEN_ENDPOINT");
            var STAGING_MOCK_IDP_USERINFO_ENDPOINT = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_USERINFO_ENDPOINT");

            // 1. Client を挿入（既存の staging Organization に紐付け）
            migrationBuilder.Sql($@"
                INSERT INTO client (client_id, client_secret, app_name, organization_id, created_at, updated_at)
                SELECT
                    '{ECCUBE2_CLIENT_ID}',
                    '{ECCUBE2_CLIENT_SECRET}',
                    'EC-CUBE2 EcAuth Plugin',
                    o.id,
                    SYSDATETIMEOFFSET(),
                    SYSDATETIMEOFFSET()
                FROM organization o
                WHERE o.code = '{STAGING_ORGANIZATION_CODE}'
            ");

            // 2. RedirectUri を挿入
            migrationBuilder.Sql($@"
                INSERT INTO redirect_uri (uri, client_id, created_at, updated_at)
                SELECT
                    '{ECCUBE2_REDIRECT_URI}',
                    c.id,
                    SYSDATETIMEOFFSET(),
                    SYSDATETIMEOFFSET()
                FROM client c
                WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
            ");

            // 3. RSA Key Pair を生成・挿入
            using var rsa = RSA.Create(2048);
            var publicKeyBytes = rsa.ExportRSAPublicKey();
            var privateKeyBytes = rsa.ExportRSAPrivateKey();
            var publicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
            var privateKeyBase64 = Convert.ToBase64String(privateKeyBytes);

            migrationBuilder.Sql($@"
                INSERT INTO rsa_key_pair (client_id, public_key, private_key)
                SELECT
                    c.id,
                    '{publicKeyBase64}',
                    '{privateKeyBase64}'
                FROM client c
                WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
            ");

            // 4. OpenIdProvider（MockIdP）を挿入
            migrationBuilder.Sql($@"
                INSERT INTO open_id_provider (
                    name, idp_client_id, idp_client_secret,
                    authorization_endpoint, token_endpoint, userinfo_endpoint,
                    created_at, updated_at, client_id
                )
                SELECT
                    '{STAGING_MOCK_IDP_APP_NAME}',
                    '{STAGING_MOCK_IDP_CLIENT_ID}',
                    '{STAGING_MOCK_IDP_CLIENT_SECRET}',
                    '{STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT}',
                    '{STAGING_MOCK_IDP_TOKEN_ENDPOINT}',
                    '{STAGING_MOCK_IDP_USERINFO_ENDPOINT}',
                    SYSDATETIMEOFFSET(),
                    SYSDATETIMEOFFSET(),
                    c.id
                FROM client c
                WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 環境変数から値を取得
            DotNetEnv.Env.TraversePath().Load();
            var ECCUBE2_CLIENT_ID = DotNetEnv.Env.GetString("ECCUBE2_CLIENT_ID");
            var STAGING_MOCK_IDP_APP_NAME = DotNetEnv.Env.GetString("STAGING_MOCK_IDP_APP_NAME");

            // 逆順で削除
            // 4. OpenIdProvider 削除
            migrationBuilder.Sql($@"
                DELETE FROM open_id_provider
                WHERE name = '{STAGING_MOCK_IDP_APP_NAME}'
                AND client_id IN (SELECT id FROM client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 3. RSA Key Pair 削除
            migrationBuilder.Sql($@"
                DELETE FROM rsa_key_pair
                WHERE client_id IN (SELECT id FROM client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 2. RedirectUri 削除
            migrationBuilder.Sql($@"
                DELETE FROM redirect_uri
                WHERE client_id IN (SELECT id FROM client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 1. Client 削除
            migrationBuilder.Sql($@"
                DELETE FROM client
                WHERE client_id = '{ECCUBE2_CLIENT_ID}'
            ");
        }
    }
}
