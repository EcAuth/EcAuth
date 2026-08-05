using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationSoftDeleteAndSandboxPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "organization",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parent_organization_id",
                table: "organization",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_sites",
                table: "account",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateIndex(
                name: "IX_organization_parent_organization_id_active",
                table: "organization",
                column: "parent_organization_id",
                unique: true,
                filter: "[parent_organization_id] IS NOT NULL AND [deleted_at] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_organization_organization_parent_organization_id",
                table: "organization",
                column: "parent_organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // 既存の本番 / サンドボックスのペアを parent_organization_id にバックフィルする。
            //
            // EXEC() でラップしているのは、--idempotent スクリプトが全マイグレーションを 1 バッチに
            // まとめるため。同一バッチ内で「このマイグレーションが追加したカラム」を参照する DML は
            // コンパイル時に解決できずエラーになるので、名前解決を実行時まで遅延させる
            // （docs/CLAUDE.md「マイグレーション設計ルール」）。
            //
            // 段階 1: 組織コードの導出規則（本番コード + "-sandbox"）で確実に一致するペア。
            // organization.code は一意なので、1 サンドボックスに対して本番は高々 1 件に決まる。
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE sandbox
SET parent_organization_id = prod.id
FROM dbo.organization AS sandbox
INNER JOIN dbo.account_organization AS ao_sandbox ON ao_sandbox.organization_id = sandbox.id
INNER JOIN dbo.account_organization AS ao_prod ON ao_prod.account_subject = ao_sandbox.account_subject
INNER JOIN dbo.organization AS prod ON prod.id = ao_prod.organization_id
WHERE sandbox.is_sandbox = 1
  AND sandbox.parent_organization_id IS NULL
  AND prod.is_sandbox = 0
  AND sandbox.code = prod.code + ''-sandbox''
');
");

            // 段階 2: 段階 1 で埋まらなかった分のうち、アカウント配下が「本番 1 件 + サンドボックス 1 件」
            // だけのケースに限って紐づける。テストサイトに本番と別ドメインを使った申込がこれに当たる。
            // 3 件以上ある、あるいは本番が複数あるアカウントは対応関係を機械的に決められないため
            // NULL のまま残す（既存の認証動作には影響しない。サンドボックス追加時に UI 側で手当てする）。
            //
            // NOT EXISTS は段階 1 で既に相方が付いた本番 Org を除外するためのもの。これが無いと
            // IX_organization_parent_organization_id_active のユニーク制約に触れてマイグレーションが失敗する。
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE sandbox
SET parent_organization_id = pair.prod_id
FROM dbo.organization AS sandbox
INNER JOIN (
    SELECT ao.account_subject,
           MIN(CASE WHEN o.is_sandbox = 0 THEN o.id END) AS prod_id,
           MIN(CASE WHEN o.is_sandbox = 1 THEN o.id END) AS sandbox_id,
           SUM(CASE WHEN o.is_sandbox = 0 THEN 1 ELSE 0 END) AS prod_count,
           SUM(CASE WHEN o.is_sandbox = 1 THEN 1 ELSE 0 END) AS sandbox_count
    FROM dbo.account_organization AS ao
    INNER JOIN dbo.organization AS o ON o.id = ao.organization_id
    GROUP BY ao.account_subject
) AS pair ON pair.sandbox_id = sandbox.id
WHERE sandbox.parent_organization_id IS NULL
  AND pair.prod_count = 1
  AND pair.sandbox_count = 1
  AND NOT EXISTS (
      SELECT 1 FROM dbo.organization AS existing
      WHERE existing.parent_organization_id = pair.prod_id
        AND existing.deleted_at IS NULL
  )
');
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organization_organization_parent_organization_id",
                table: "organization");

            migrationBuilder.DropIndex(
                name: "IX_organization_parent_organization_id_active",
                table: "organization");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "organization");

            migrationBuilder.DropColumn(
                name: "parent_organization_id",
                table: "organization");

            migrationBuilder.DropColumn(
                name: "max_sites",
                table: "account");
        }
    }
}
