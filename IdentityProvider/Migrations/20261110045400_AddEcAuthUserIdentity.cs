using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddEcAuthUserIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ecauth_user_organization_id_email_hash",
                table: "ecauth_user");

            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "ecauth_user",
                type: "varchar(512)",
                unicode: false,
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "ecauth_user",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ecauth_user_identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ecauth_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    issuer_key = table.Column<string>(type: "varchar(520)", unicode: false, maxLength: 520, nullable: false),
                    external_id = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    client_id = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ecauth_user_identity", x => x.id);
                    table.ForeignKey(
                        name: "FK_ecauth_user_identity_ecauth_user_ecauth_subject",
                        column: x => x.ecauth_subject,
                        principalTable: "ecauth_user",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_client_id_email_hash",
                table: "ecauth_user",
                columns: new[] { "client_id", "email_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_organization_id",
                table: "ecauth_user",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_identity_client_id",
                table: "ecauth_user_identity",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_identity_ecauth_subject",
                table: "ecauth_user_identity",
                column: "ecauth_subject");

            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_identity_issuer_key_external_id",
                table: "ecauth_user_identity",
                columns: new[] { "issuer_key", "external_id" },
                unique: true);

            // ---- backfill（EcAuthDocs#122、expand リリース）----
            //
            // 旧 external_idp_mapping は provider 名（open_id_provider.name）で区切られており、名前は
            // Client 間で衝突しうる。provider 名と「ユーザーの Organization に属する Client」で
            // open_id_provider を引き、1 行に決まるものだけを移す。決まらない行は残しておけば、
            // 次のログインで UserService の遅延移行（mapping フォールバック）が Client を確定して移す。
            //
            // external_id は C# の ExternalIdHasher.HashCaseSensitive と同じ値にする:
            //   hash = Convert.ToHexString(SHA256(UTF8(sub.Trim())))   // 大文字 hex 64 文字、小文字化しない
            // T-SQL では UTF-8 照合順序で varchar に変換して HASHBYTES に渡す（nvarchar のままだと UTF-16LE に
            // なり一致しない。20260622210330_HashB2BUserExternalId と同じ書き方）。T-SQL の LTRIM / RTRIM は
            // ASCII スペースしか除かず C# の Trim と乖離するので、Trim の差が出ない値（ASCII 印字可能文字だけで、
            // 前後に空白が無い）だけを対象にする。OIDC の sub は ASCII なので通常はすべて対象になる。
            //
            // 冪等: client_id は NULL の行だけ、identity は NOT EXISTS の行だけ書く（transition リリースの
            // 追いつき backfill で同じ SQL を再実行できる）。追加した列を参照するので EXEC() で包む。
            migrationBuilder.Sql(@"
                EXEC('
                    WITH cand AS (
                        SELECT u.id AS user_id, c.client_id
                        FROM dbo.ecauth_user u
                        JOIN dbo.external_idp_mapping m ON m.ecauth_subject = u.subject
                        JOIN dbo.open_id_provider p ON p.name = m.external_provider
                        JOIN dbo.client c ON c.id = p.client_id AND c.organization_id = u.organization_id
                    ), uniq AS (
                        SELECT user_id, MIN(client_id) AS client_id
                        FROM cand
                        GROUP BY user_id
                        HAVING COUNT(DISTINCT client_id) = 1
                    )
                    UPDATE u SET client_id = uniq.client_id
                    FROM dbo.ecauth_user u
                    JOIN uniq ON uniq.user_id = u.id
                    WHERE u.client_id IS NULL;
                ');

                EXEC('
                    WITH cand AS (
                        SELECT m.id AS mapping_id, u.subject, p.id AS provider_id, c.client_id,
                               m.external_subject, m.created_at
                        FROM dbo.external_idp_mapping m
                        JOIN dbo.ecauth_user u ON u.subject = m.ecauth_subject
                        JOIN dbo.open_id_provider p ON p.name = m.external_provider
                        JOIN dbo.client c ON c.id = p.client_id AND c.organization_id = u.organization_id
                    ), uniq AS (
                        SELECT mapping_id, MIN(subject) AS subject, MIN(provider_id) AS provider_id,
                               MIN(client_id) AS client_id, MIN(external_subject) AS external_subject,
                               MIN(created_at) AS created_at
                        FROM cand
                        GROUP BY mapping_id
                        HAVING COUNT(*) = 1
                    ), hashed AS (
                        SELECT q.subject, q.client_id, q.created_at,
                               ''idp:'' + CONVERT(varchar(20), q.provider_id) AS issuer_key,
                               CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                                   CONVERT(varchar(1020), q.external_subject COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2) AS external_id
                        FROM uniq q
                        WHERE PATINDEX(N''%[^ -~]%'', q.external_subject COLLATE Latin1_General_100_BIN2) = 0
                          AND q.external_subject <> N''''
                          AND LEFT(q.external_subject, 1) <> N'' ''
                          AND RIGHT(q.external_subject, 1) <> N'' ''
                    )
                    INSERT INTO dbo.ecauth_user_identity (ecauth_subject, issuer_key, external_id, client_id, created_at)
                    SELECT h.subject, h.issuer_key, h.external_id, h.client_id, h.created_at
                    FROM hashed h
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dbo.ecauth_user_identity i
                        WHERE i.issuer_key = h.issuer_key AND i.external_id = h.external_id);
                ');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ecauth_user_identity");

            migrationBuilder.DropIndex(
                name: "IX_ecauth_user_client_id_email_hash",
                table: "ecauth_user");

            migrationBuilder.DropIndex(
                name: "IX_ecauth_user_organization_id",
                table: "ecauth_user");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "ecauth_user");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "ecauth_user");

            // 一意制約へは戻さない。この変更の後に作られた重複（メールアドレスの無いユーザーが複数、
            // 同じメールアドレスで別 IdP から入ったユーザーなど）で CREATE UNIQUE INDEX が失敗し、
            // ロールバック自体ができなくなるため（EcAuth#521 の Down と同じ判断）。
            migrationBuilder.CreateIndex(
                name: "IX_ecauth_user_organization_id_email_hash",
                table: "ecauth_user",
                columns: new[] { "organization_id", "email_hash" });
        }
    }
}
