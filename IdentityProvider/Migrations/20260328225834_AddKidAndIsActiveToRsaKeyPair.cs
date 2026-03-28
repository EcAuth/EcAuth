using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddKidAndIsActiveToRsaKeyPair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. ユニークインデックスを通常インデックスに変更（1:N 対応）
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair");

            // 2. created_at カラム追加
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "rsa_key_pair",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "SYSDATETIMEOFFSET()");

            // 3. is_active カラム追加（デフォルト true: 既存鍵はすべてアクティブ）
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "rsa_key_pair",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // 4. kid カラム追加
            migrationBuilder.AddColumn<string>(
                name: "kid",
                table: "rsa_key_pair",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // 5. 既存データの kid に UUID を設定
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'kid')
                BEGIN
                    EXEC(N'UPDATE dbo.rsa_key_pair SET kid = LOWER(NEWID()) WHERE kid = ''''')
                END
            ");

            // 6. organization_id に通常インデックスを作成
            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair",
                column: "organization_id");

            // 7. (organization_id, kid) にユニークインデックスを作成
            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_organization_id_kid",
                table: "rsa_key_pair",
                columns: new[] { "organization_id", "kid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id_kid",
                table: "rsa_key_pair");

            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "rsa_key_pair");

            migrationBuilder.DropColumn(
                name: "is_active",
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
