using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdpToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_idp_token",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ecauth_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    external_provider = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    access_token = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    refresh_token = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_idp_token", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_idp_token_ecauth_user_ecauth_subject",
                        column: x => x.ecauth_subject,
                        principalTable: "ecauth_user",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_idp_token_ecauth_subject_external_provider",
                table: "external_idp_token",
                columns: new[] { "ecauth_subject", "external_provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_idp_token_expires_at",
                table: "external_idp_token",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_idp_token");
        }
    }
}
