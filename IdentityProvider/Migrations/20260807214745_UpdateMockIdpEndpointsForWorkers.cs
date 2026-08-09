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
    ///
    /// <para>
    /// <b>制約（環境変数が無い環境では no-op として記録される）</b>:
    /// 必要な環境変数が揃っていない環境では SQL を発行せずに完了し、それでも
    /// <c>__EFMigrationsHistory</c> には適用済みとして記録される。後から環境変数を
    /// 追加しても再実行されないため、エンドポイントを変えたい場合は新しい
    /// マイグレーションを追加するか、DB を直接更新すること。
    /// </para>
    /// <para>
    /// 変数が欠けているときに例外を投げる案は採らない。CI の
    /// <c>dotnet_tests.yml</c> の <c>verify-idempotent-script</c> ジョブは MockIdP 系の
    /// 環境変数を持たずに <c>dotnet ef migrations script --idempotent</c> を実行するため、
    /// スクリプト生成そのものが失敗する。また 1 つの <c>Up()</c> で dev / staging /
    /// production を扱う以上、ある環境の変数が無いのは正常な状態である。
    /// </para>
    /// <para>
    /// <b>生成したスクリプトを環境間で再利用しないこと</b>:
    /// 値は <c>dotnet ef migrations script</c> 実行時の環境変数から埋め込まれる
    /// （DotNetEnv を使う既存マイグレーション群と同じ性質）。staging 用に生成した
    /// スクリプトを production へ流用すると、staging のエンドポイントが書き込まれる。
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

            // EXEC() でラップして名前解決を実行時まで遅延させ、テーブル名は dbo. で修飾する
            // （CLAUDE.md「マイグレーション設計ルール」）。冪等スクリプトは全マイグレーションが
            // 1 バッチにまとめられるため、後続のマイグレーションが列を変更してもここが
            // コンパイルエラーにならないようにする。
            migrationBuilder.Sql($@"
                EXEC(N'
                    UPDATE dbo.open_id_provider
                    SET authorization_endpoint = ''{EscapeInsideExec(authorizationEndpoint)}'',
                        token_endpoint = ''{EscapeInsideExec(tokenEndpoint)}'',
                        userinfo_endpoint = ''{EscapeInsideExec(userinfoEndpoint)}'',
                        updated_at = SYSDATETIMEOFFSET()
                    WHERE name = ''{EscapeInsideExec(appName)}''
                ')
            ");
        }

        /// <summary>SQL リテラル用にシングルクォートをエスケープする。</summary>
        private static string Escape(string value) => value.Replace("'", "''");

        /// <summary>
        /// <c>EXEC(N'...')</c> の内側に埋め込む SQL リテラル用にエスケープする。
        /// 内側の SQL リテラルとしての二重化に加え、EXEC の文字列リテラルとしての二重化も
        /// 必要になるため <see cref="Escape"/> を 2 回適用する（元の <c>'</c> は <c>''''</c> になる）。
        /// </summary>
        private static string EscapeInsideExec(string value) => Escape(Escape(value));
    }
}
