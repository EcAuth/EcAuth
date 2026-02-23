using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexToClientClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "varchar(512)",
                unicode: false,
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            // ユニークインデックス作成前に重複する client_id を持つレコードを削除（最小 id を残す）
            // FK Restrict の子テーブルを先に削除してから親テーブルを削除する
            migrationBuilder.Sql(@"
                -- FK Restrict: access_token, authorization_code, web_authn_challenge
                DELETE FROM dbo.access_token
                WHERE client_id IN (
                    SELECT id FROM dbo.client
                    WHERE id NOT IN (SELECT MIN(id) FROM dbo.client GROUP BY client_id)
                );

                DELETE FROM dbo.authorization_code
                WHERE client_id IN (
                    SELECT id FROM dbo.client
                    WHERE id NOT IN (SELECT MIN(id) FROM dbo.client GROUP BY client_id)
                );

                DELETE FROM dbo.web_authn_challenge
                WHERE client_id IN (
                    SELECT id FROM dbo.client
                    WHERE id NOT IN (SELECT MIN(id) FROM dbo.client GROUP BY client_id)
                );

                -- FK No Action (default): open_id_provider
                -- open_id_provider_scope は open_id_provider に Cascade なので自動削除される
                DELETE FROM dbo.open_id_provider
                WHERE client_id IN (
                    SELECT id FROM dbo.client
                    WHERE id NOT IN (SELECT MIN(id) FROM dbo.client GROUP BY client_id)
                );

                -- redirect_uri, rsa_key_pair は Cascade なので自動削除される
                DELETE FROM dbo.client
                WHERE id NOT IN (
                    SELECT MIN(id) FROM dbo.client GROUP BY client_id
                );
            ");

            migrationBuilder.CreateIndex(
                name: "IX_client_client_id",
                table: "client",
                column: "client_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_client_client_id",
                table: "client");

            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(512)",
                oldUnicode: false,
                oldMaxLength: 512);
        }
    }
}
