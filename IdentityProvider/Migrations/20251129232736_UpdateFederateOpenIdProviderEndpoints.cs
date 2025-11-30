using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class UpdateFederateOpenIdProviderEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 環境変数から値を取得
            DotNetEnv.Env.TraversePath().Load();
            var FEDERATE_OAUTH2_APP_NAME = DotNetEnv.Env.GetString("FEDERATE_OAUTH2_APP_NAME");
            var FEDERATE_OAUTH2_AUTHORIZATION_ENDPOINT = DotNetEnv.Env.GetString("FEDERATE_OAUTH2_AUTHORIZATION_ENDPOINT");
            var FEDERATE_OAUTH2_TOKEN_ENDPOINT = DotNetEnv.Env.GetString("FEDERATE_OAUTH2_TOKEN_ENDPOINT");
            var FEDERATE_OAUTH2_USERINFO_ENDPOINT = DotNetEnv.Env.GetString("FEDERATE_OAUTH2_USERINFO_ENDPOINT");

            // Federate OAuth2 プロバイダーのエンドポイントを更新
            // 注意: URL にクエリパラメータを含めないこと（AuthorizationController で ? を付加するため）
            migrationBuilder.Sql($@"
                UPDATE open_id_provider
                SET authorization_endpoint = '{FEDERATE_OAUTH2_AUTHORIZATION_ENDPOINT}',
                    token_endpoint = '{FEDERATE_OAUTH2_TOKEN_ENDPOINT}',
                    userinfo_endpoint = '{FEDERATE_OAUTH2_USERINFO_ENDPOINT}',
                    updated_at = SYSDATETIMEOFFSET()
                WHERE name = '{FEDERATE_OAUTH2_APP_NAME}'
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 環境変数から値を取得
            DotNetEnv.Env.TraversePath().Load();
            var FEDERATE_OAUTH2_APP_NAME = DotNetEnv.Env.GetString("FEDERATE_OAUTH2_APP_NAME");

            // 元のローカル開発用エンドポイントに戻す
            migrationBuilder.Sql($@"
                UPDATE open_id_provider
                SET authorization_endpoint = 'https://localhost:9091/authorization',
                    token_endpoint = 'https://localhost:9091/token',
                    userinfo_endpoint = 'https://localhost:9091/userinfo',
                    updated_at = SYSDATETIMEOFFSET()
                WHERE name = '{FEDERATE_OAUTH2_APP_NAME}'
            ");
        }
    }
}
