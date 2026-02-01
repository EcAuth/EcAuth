using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectTypeToTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subject_new",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "subject_type",
                table: "authorization_code",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "access_token",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "subject_type",
                table: "access_token",
                type: "int",
                nullable: true);

            // 既存データの変換: AccessToken (B2C)
            // EXEC() で動的SQLにすることで、idempotent スクリプト生成時の
            // バッチコンパイルエラーを回避（subject カラムの遅延名前解決）
            // IF EXISTS で RemoveLegacySubjectColumns 適用済みの場合にも対応
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1
                           FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.access_token')
                             AND name = 'ecauth_subject')
                BEGIN
                    EXEC(N'UPDATE dbo.access_token
                    SET subject = ecauth_subject, subject_type = 0
                    WHERE ecauth_subject IS NOT NULL;');
                END
            ");

            // 既存データの変換: AuthorizationCode (B2C)
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1
                           FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.authorization_code')
                             AND name = 'ecauth_subject')
                BEGIN
                    EXEC(N'UPDATE dbo.authorization_code
                    SET subject_new = ecauth_subject, subject_type = 0
                    WHERE ecauth_subject IS NOT NULL;');
                END
            ");

            // 既存データの変換: AuthorizationCode (B2B)
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1
                           FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.authorization_code')
                             AND name = 'b2b_subject')
                BEGIN
                    EXEC(N'UPDATE dbo.authorization_code
                    SET subject_new = b2b_subject, subject_type = 1
                    WHERE b2b_subject IS NOT NULL;');
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subject_new",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "subject_type",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "access_token");

            migrationBuilder.DropColumn(
                name: "subject_type",
                table: "access_token");
        }
    }
}
