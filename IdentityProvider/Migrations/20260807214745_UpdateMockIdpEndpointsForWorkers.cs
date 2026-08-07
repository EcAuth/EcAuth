using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <summary>
    /// MockIdP の Cloudflare Workers 移行にともない、open_id_provider の
    /// エンドポイントを環境変数の値へ更新する。
    ///
    /// <para>
    /// このマイグレーションが必要な理由: <c>OrganizationClientSeeder.SeedOpenIdProviderAsync</c>
    /// は同名レコードが既にあると何もせず抜けるため、環境変数（1Password）の
    /// エンドポイントを変更しても既存行には反映されない。新規構築の環境では
    /// シーダーが正しい値で作成するので、このマイグレーションは実質 no-op になる。
    /// </para>
    ///
    /// <para>
    /// 対象は実行環境に設定されている変数のみ。staging の DB に対して実行すれば
    /// staging の行だけが更新される。
    /// </para>
    /// </summary>
    public partial class UpdateMockIdpEndpointsForWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DotNetEnv.Env.TraversePath().Load();

            // dev: provider 名は InsertFederateOpenIdProvider で作られた federate-oauth2
            UpdateEndpoints(
                migrationBuilder,
                appName: DotNetEnv.Env.GetString("FEDERATE_OAUTH2_APP_NAME"),
                authorizationEndpoint: DotNetEnv.Env.GetString("FEDERATE_OAUTH2_AUTHORIZATION_ENDPOINT"),
                tokenEndpoint: DotNetEnv.Env.GetString("FEDERATE_OAUTH2_TOKEN_ENDPOINT"),
                userinfoEndpoint: DotNetEnv.Env.GetString("FEDERATE_OAUTH2_USERINFO_ENDPOINT"));

            // staging: provider 名は OrganizationClientSeeder が作成
            UpdateEndpoints(
                migrationBuilder,
                appName: DotNetEnv.Env.GetString("STAGING_MOCK_IDP_APP_NAME"),
                authorizationEndpoint: DotNetEnv.Env.GetString("STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT"),
                tokenEndpoint: DotNetEnv.Env.GetString("STAGING_MOCK_IDP_TOKEN_ENDPOINT"),
                userinfoEndpoint: DotNetEnv.Env.GetString("STAGING_MOCK_IDP_USERINFO_ENDPOINT"));

            // production
            UpdateEndpoints(
                migrationBuilder,
                appName: DotNetEnv.Env.GetString("PROD_MOCK_IDP_APP_NAME"),
                authorizationEndpoint: DotNetEnv.Env.GetString("PROD_MOCK_IDP_AUTHORIZATION_ENDPOINT"),
                tokenEndpoint: DotNetEnv.Env.GetString("PROD_MOCK_IDP_TOKEN_ENDPOINT"),
                userinfoEndpoint: DotNetEnv.Env.GetString("PROD_MOCK_IDP_USERINFO_ENDPOINT"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 変更前の値（Azure Container Apps の URL）は環境変数からも DB からも
            // 復元できないため、意図的に no-op とする。
            // 切り戻す場合は 1Password の mockidp-* アイテムの
            // authorization_endpoint / token_endpoint / userinfo_endpoint を
            // 旧 URL に戻したうえで、このマイグレーションを再適用する。
        }

        /// <summary>
        /// 指定した provider のエンドポイントを更新する。
        /// 必要な値が 1 つでも欠けている環境はスキップする。
        /// </summary>
        private static void UpdateEndpoints(
            MigrationBuilder migrationBuilder,
            string appName,
            string authorizationEndpoint,
            string tokenEndpoint,
            string userinfoEndpoint)
        {
            if (string.IsNullOrWhiteSpace(appName)
                || string.IsNullOrWhiteSpace(authorizationEndpoint)
                || string.IsNullOrWhiteSpace(tokenEndpoint)
                || string.IsNullOrWhiteSpace(userinfoEndpoint))
            {
                return;
            }

            migrationBuilder.Sql($@"
                UPDATE open_id_provider
                SET authorization_endpoint = '{Escape(authorizationEndpoint)}',
                    token_endpoint = '{Escape(tokenEndpoint)}',
                    userinfo_endpoint = '{Escape(userinfoEndpoint)}',
                    updated_at = SYSDATETIMEOFFSET()
                WHERE name = '{Escape(appName)}'
            ");
        }

        /// <summary>SQL リテラル用にシングルクォートをエスケープする。</summary>
        private static string Escape(string value) => value.Replace("'", "''");
    }
}
