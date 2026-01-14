using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddB2BSubjectToAuthorizationCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code");

            migrationBuilder.AlterColumn<string>(
                name: "ecauth_subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<string>(
                name: "b2b_subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_authorization_code_b2b_subject",
                table: "authorization_code",
                column: "b2b_subject");

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_b2b_user_b2b_subject",
                table: "authorization_code",
                column: "b2b_subject",
                principalTable: "b2b_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code",
                column: "ecauth_subject",
                principalTable: "ecauth_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_b2b_user_b2b_subject",
                table: "authorization_code");

            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code");

            migrationBuilder.DropIndex(
                name: "IX_authorization_code_b2b_subject",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "b2b_subject",
                table: "authorization_code");

            migrationBuilder.AlterColumn<string>(
                name: "ecauth_subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code",
                column: "ecauth_subject",
                principalTable: "ecauth_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
