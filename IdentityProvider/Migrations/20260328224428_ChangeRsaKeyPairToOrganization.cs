using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class ChangeRsaKeyPairToOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 既存の外部キー制約を削除
            migrationBuilder.DropForeignKey(
                name: "FK_rsa_key_pair_client_client_id",
                table: "rsa_key_pair");

            // 2. organization_id カラムを追加（NULL許容で一時追加）
            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "rsa_key_pair",
                type: "int",
                nullable: true);

            // 3. 既存データ移行: client.organization_id を rsa_key_pair.organization_id にコピー
            // EXEC() でラ���プして名前解決��実行時まで遅延
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'client_id')
                AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'organization_id')
                BEGIN
                    EXEC(N'UPDATE dbo.rsa_key_pair SET organization_id = c.organization_id FROM dbo.rsa_key_pair r INNER JOIN dbo.client c ON r.client_id = c.id')
                END
            ");

            // 4. backfill 失敗検知: organization_id が NULL のレコードが残っていたらエラー
            // EXEC() でラップして名前解決を実行時まで遅延（idempotent スクリプト対応）
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'organization_id')
                BEGIN
                    EXEC(N'IF EXISTS (SELECT 1 FROM dbo.rsa_key_pair WHERE organization_id IS NULL) THROW 50000, ''rsa_key_pair.organization_id backfill failed. Check client.organization_id / orphaned rsa_key_pair rows.'', 1;')
                END
            ");

            // 5. 同一 organization_id の重複キーペアを削除（最小IDのみ残す）
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'organization_id')
                BEGIN
                    EXEC(N'DELETE FROM dbo.rsa_key_pair WHERE id NOT IN (SELECT MIN(id) FROM dbo.rsa_key_pair GROUP BY organization_id)')
                END
            ");

            // 6. NOT NULL 制約を追加
            migrationBuilder.AlterColumn<int>(
                name: "organization_id",
                table: "rsa_key_pair",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            // 7. client_id のインデックスを削除
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_client_id",
                table: "rsa_key_pair");

            // 8. client_id カラムを削除
            migrationBuilder.DropColumn(
                name: "client_id",
                table: "rsa_key_pair");

            // 9. organization_id にユニークインデックスを作成
            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair",
                column: "organization_id",
                unique: true);

            // 10. 外部キー制約を追加
            migrationBuilder.AddForeignKey(
                name: "FK_rsa_key_pair_organization_organization_id",
                table: "rsa_key_pair",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. 外部キー制約を削除
            migrationBuilder.DropForeignKey(
                name: "FK_rsa_key_pair_organization_organization_id",
                table: "rsa_key_pair");

            // 2. client_id カラムを追加（NULL許容で一時追加）
            migrationBuilder.AddColumn<int>(
                name: "client_id",
                table: "rsa_key_pair",
                type: "int",
                nullable: true);

            // 3. データ復元: organization の最初の client を rsa_key_pair.client_id に設定
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'organization_id')
                AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rsa_key_pair') AND name = 'client_id')
                BEGIN
                    EXEC(N'UPDATE dbo.rsa_key_pair SET client_id = c.id FROM dbo.rsa_key_pair r INNER JOIN (SELECT MIN(id) AS id, organization_id FROM dbo.client GROUP BY organization_id) c ON r.organization_id = c.organization_id')
                END
            ");

            // 4. NOT NULL 制約を追加
            migrationBuilder.AlterColumn<int>(
                name: "client_id",
                table: "rsa_key_pair",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            // 5. organization_id のインデックスを削除
            migrationBuilder.DropIndex(
                name: "IX_rsa_key_pair_organization_id",
                table: "rsa_key_pair");

            // 6. organization_id カラムを削除
            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "rsa_key_pair");

            // 7. client_id にユニークインデックスを作成
            migrationBuilder.CreateIndex(
                name: "IX_rsa_key_pair_client_id",
                table: "rsa_key_pair",
                column: "client_id",
                unique: true);

            // 8. 外部キー制約を追加
            migrationBuilder.AddForeignKey(
                name: "FK_rsa_key_pair_client_client_id",
                table: "rsa_key_pair",
                column: "client_id",
                principalTable: "client",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
