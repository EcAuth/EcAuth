using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class BindPasskeyRegistrationTokenToSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "passkey_registration_token",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_id",
                table: "passkey_registration_token");
        }
    }
}
