using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupRequestTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signup_request",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    confirm_token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    organization_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    contact_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    production_site_url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    test_site_url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ec_cube_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    terms_version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    privacy_version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    cookie_version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    tenant_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signup_request", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_signup_request_confirm_token_hash",
                table: "signup_request",
                column: "confirm_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signup_request_tenant_name_confirmed_at",
                table: "signup_request",
                columns: new[] { "tenant_name", "confirmed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signup_request");
        }
    }
}
