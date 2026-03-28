using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddKidToRsaKeyPairAndAllowMultipleKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. ユニークインデックスを通常インデックスに変更（1:N 対応）
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair");

            // 2. kid カラムを追加（一時的に空文字をデフォルト値に）
            migrationBuilder.AddColumn<string>(
                name: "kid",
                table: "rsa_key_pair",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // 3. 既存データの kid に UUID を設定
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'kid')
                BEGIN
                    EXEC(N'UPDATE dbo.rsa_key_pair SET kid = LOWER(NEWID()) WHERE kid = ''''')
                END
            ");

            // 4. organization_id に通常インデックスを作成
            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair");

            migrationBuilder.DropColumn(
                name: "kid",
                table: "rsa_key_pair");

            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair",
                column: "organization_id",
                unique: true);
        }
    }
}
