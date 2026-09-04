using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddB2BUserIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // b2b_user の (organization_id, external_id) を非一意に緩和する（EcAuthDocs#110）。
            //
            // 識別子の一意性は b2b_user_identity の (issuer_key, external_id) へ移す。旧索引を
            // UNIQUE のまま残すと、同一 Organization に発行元の異なる Client がぶら下がる構成で
            // 2 人目の b2b_user INSERT が一意違反で落ち、本移行の目的（EC-CUBE の member_id=1 と
            // WordPress の user_id=1 の共存）が実 DB では成立しない。
            // 索引自体は移行期間中のフォールバック検索のために非一意で残す。
            migrationBuilder.DropIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user");

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user",
                columns: new[] { "organization_id", "external_id" });

            migrationBuilder.CreateTable(
                name: "b2b_user_identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    b2b_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    issuer_key = table.Column<string>(type: "varchar(520)", unicode: false, maxLength: 520, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    client_id = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_b2b_user_identity", x => x.id);
                    table.ForeignKey(
                        name: "FK_b2b_user_identity_b2b_user_b2b_subject",
                        column: x => x.b2b_subject,
                        principalTable: "b2b_user",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_identity_b2b_subject",
                table: "b2b_user_identity",
                column: "b2b_subject");

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_identity_client_id",
                table: "b2b_user_identity",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_identity_issuer_key_external_id",
                table: "b2b_user_identity",
                columns: new[] { "issuer_key", "external_id" },
                unique: true);

            // 既存 b2b_user.external_id を b2b_user_identity へ移送する（EcAuthDocs#110）。
            //
            // issuer_key は「その Organization が持つ唯一の Client」から導出する。b2b_user 単体では
            // 発行元を特定できないが、本番実測（2026-08-29）で
            //   - B2B Client を 2 つ以上持つ Organization: 0 件
            //   - user_type=admin の 80 件はすべて B2B Client を 1 つ持つ Org に所属
            //   - user_type=account_owner の 15 件はすべて accounts / stg-accounts に所属し、
            //     各 Org は Account 型 Client を 1 つ持つ
            // であることを確認済みで、例外 0 件で決定的に補完できる。
            //
            // HAVING COUNT(*) = 1 で「唯一であること」を条件に入れているため、Client が 0 個または
            // 複数ある Organization のユーザーは意図的に移送しない。その場合も
            // b2b_user.external_id 経由のフォールバック（B2BPasskeyService.ResolveByExternalIdAsync）で
            // 従来どおり解決できるため、ログイン不能にはならない。
            //
            // 注意: CLAUDE.md のルールに従い、本マイグレーションで作成した表・列を参照する DML は
            //       EXEC() でラップして名前解決を実行時まで遅延させる（idempotent script では全
            //       マイグレーションが 1 バッチでコンパイルされ、コンパイル時点では表が存在しないため）。
            //       参照元の b2b_user.external_id 側は sys.columns で存在を確認する（当該列を落とす
            //       後続マイグレーションが適用済みの環境でも冪等スクリプトが通るようにするため）。
            //
            // 冪等性: __EFMigrationsHistory により Up は一度だけ実行されるが、部分適用からの復旧を
            //         考慮して NOT EXISTS ガードも入れている。
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.b2b_user')
                           AND name = 'external_id')
                BEGIN
                    -- account_owner（accounts / stg-accounts）: 発行元は Account 型 Client
                    EXEC('
                        INSERT INTO dbo.b2b_user_identity (b2b_subject, issuer_key, external_id, client_id, created_at)
                        SELECT u.subject, ''client:'' + c.client_id, u.external_id, c.client_id, SYSDATETIMEOFFSET()
                        FROM dbo.b2b_user u
                        INNER JOIN (
                            SELECT organization_id, MIN(client_id) AS client_id
                            FROM dbo.client
                            WHERE subject_type = 2 AND organization_id IS NOT NULL
                            GROUP BY organization_id
                            HAVING COUNT(*) = 1
                        ) c ON c.organization_id = u.organization_id
                        WHERE u.user_type = ''account_owner''
                          AND u.external_id <> ''''
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.b2b_user_identity i
                              WHERE i.issuer_key = ''client:'' + c.client_id
                                AND i.external_id = u.external_id)
                    ');

                    -- それ以外（EC-CUBE プラグイン等の一般管理者）: 発行元は B2B 型 Client
                    EXEC('
                        INSERT INTO dbo.b2b_user_identity (b2b_subject, issuer_key, external_id, client_id, created_at)
                        SELECT u.subject, ''client:'' + c.client_id, u.external_id, c.client_id, SYSDATETIMEOFFSET()
                        FROM dbo.b2b_user u
                        INNER JOIN (
                            SELECT organization_id, MIN(client_id) AS client_id
                            FROM dbo.client
                            WHERE subject_type = 1 AND organization_id IS NOT NULL
                            GROUP BY organization_id
                            HAVING COUNT(*) = 1
                        ) c ON c.organization_id = u.organization_id
                        WHERE u.user_type <> ''account_owner''
                          AND u.external_id <> ''''
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.b2b_user_identity i
                              WHERE i.issuer_key = ''client:'' + c.client_id
                                AND i.external_id = u.external_id)
                    ');
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "b2b_user_identity");

            migrationBuilder.DropIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user");

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user",
                columns: new[] { "organization_id", "external_id" },
                unique: true);
        }
    }
}
