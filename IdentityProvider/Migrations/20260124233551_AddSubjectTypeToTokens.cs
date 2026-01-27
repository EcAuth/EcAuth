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
            migrationBuilder.Sql(@"
                UPDATE access_token
                SET subject = ecauth_subject, subject_type = 0
                WHERE ecauth_subject IS NOT NULL;
            ");

            // 既存データの変換: AuthorizationCode (B2C)
            migrationBuilder.Sql(@"
                UPDATE authorization_code
                SET subject_new = ecauth_subject, subject_type = 0
                WHERE ecauth_subject IS NOT NULL;
            ");

            // 既存データの変換: AuthorizationCode (B2B)
            migrationBuilder.Sql(@"
                UPDATE authorization_code
                SET subject_new = b2b_subject, subject_type = 1
                WHERE b2b_subject IS NOT NULL;
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
