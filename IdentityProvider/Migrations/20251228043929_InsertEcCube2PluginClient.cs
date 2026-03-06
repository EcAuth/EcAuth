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
                IF NOT EXISTS (SELECT 1 FROM dbo.client c INNER JOIN dbo.organization o ON c.organization_id = o.id WHERE c.client_id = '{ECCUBE2_CLIENT_ID}' AND o.code = '{STAGING_ORGANIZATION_CODE}')
                BEGIN
                    INSERT INTO dbo.client (client_id, client_secret, app_name, organization_id, created_at, updated_at)
                    SELECT
                        '{ECCUBE2_CLIENT_ID}',
                        '{ECCUBE2_CLIENT_SECRET}',
                        'EC-CUBE2 EcAuth Plugin',
                        o.id,
                        SYSDATETIMEOFFSET(),
                        SYSDATETIMEOFFSET()
                    FROM dbo.organization o
                    WHERE o.code = '{STAGING_ORGANIZATION_CODE}'
                END
            ");

            // 2. RedirectUri を挿入
            migrationBuilder.Sql($@"
                IF NOT EXISTS (SELECT 1 FROM dbo.redirect_uri r INNER JOIN dbo.client c ON r.client_id = c.id WHERE r.uri = '{ECCUBE2_REDIRECT_URI}' AND c.client_id = '{ECCUBE2_CLIENT_ID}')
                BEGIN
                    INSERT INTO dbo.redirect_uri (uri, client_id, created_at, updated_at)
                    SELECT
                        '{ECCUBE2_REDIRECT_URI}',
                        c.id,
                        SYSDATETIMEOFFSET(),
                        SYSDATETIMEOFFSET()
                    FROM dbo.client c
                    WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
                END
            ");

            // 3. RSA Key Pair を生成・挿入
            using var rsa = RSA.Create(2048);
            var publicKeyBytes = rsa.ExportRSAPublicKey();
            var privateKeyBytes = rsa.ExportRSAPrivateKey();
            var publicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
            var privateKeyBase64 = Convert.ToBase64String(privateKeyBytes);

            migrationBuilder.Sql($@"
                INSERT INTO dbo.rsa_key_pair (client_id, public_key, private_key)
                SELECT
                    c.id,
                    '{publicKeyBase64}',
                    '{privateKeyBase64}'
                FROM dbo.client c
                WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
                AND NOT EXISTS (SELECT 1 FROM dbo.rsa_key_pair r WHERE r.client_id = c.id)
            ");

            // 4. OpenIdProvider（MockIdP）を挿入
            migrationBuilder.Sql($@"
                IF NOT EXISTS (SELECT 1 FROM dbo.open_id_provider p INNER JOIN dbo.client c ON p.client_id = c.id WHERE p.name = '{STAGING_MOCK_IDP_APP_NAME}' AND c.client_id = '{ECCUBE2_CLIENT_ID}')
                BEGIN
                    INSERT INTO dbo.open_id_provider (
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
                    FROM dbo.client c
                    WHERE c.client_id = '{ECCUBE2_CLIENT_ID}'
                END
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
                DELETE FROM dbo.open_id_provider
                WHERE name = '{STAGING_MOCK_IDP_APP_NAME}'
                AND client_id IN (SELECT id FROM dbo.client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 3. RSA Key Pair 削除
            migrationBuilder.Sql($@"
                DELETE FROM dbo.rsa_key_pair
                WHERE client_id IN (SELECT id FROM dbo.client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 2. RedirectUri 削除
            migrationBuilder.Sql($@"
                DELETE FROM dbo.redirect_uri
                WHERE client_id IN (SELECT id FROM dbo.client WHERE client_id = '{ECCUBE2_CLIENT_ID}')
            ");

            // 1. Client 削除
            migrationBuilder.Sql($@"
                DELETE FROM dbo.client
                WHERE client_id = '{ECCUBE2_CLIENT_ID}'
            ");
        }
    }
}
