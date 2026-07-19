using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddPkceToAuthorizationCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code_challenge",
                table: "authorization_code",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "code_challenge_method",
                table: "authorization_code",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_challenge",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "code_challenge_method",
                table: "authorization_code");
        }
    }
}
