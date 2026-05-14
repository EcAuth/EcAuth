using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountManagementSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "password",
                table: "account");

            migrationBuilder.AddColumn<int>(
                name: "subject_type",
                table: "client",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "account",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "account",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "account",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "account",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "account",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_account_subject",
                table: "account",
                column: "subject");

            migrationBuilder.CreateTable(
                name: "account_organization",
                columns: table => new
                {
                    account_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    organization_id = table.Column<int>(type: "int", nullable: false),
                    role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_organization", x => new { x.account_subject, x.organization_id });
                    table.ForeignKey(
                        name: "FK_account_organization_account_account_subject",
                        column: x => x.account_subject,
                        principalTable: "account",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_account_organization_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "magic_login_token",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    account_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    requested_email_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    requested_ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    requested_user_agent = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_magic_login_token", x => x.id);
                    table.ForeignKey(
                        name: "FK_magic_login_token_account_account_subject",
                        column: x => x.account_subject,
                        principalTable: "account",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_organization_id_email",
                table: "account",
                columns: new[] { "organization_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_organization_organization_id",
                table: "account_organization",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_magic_login_token_account_subject",
                table: "magic_login_token",
                column: "account_subject");

            migrationBuilder.CreateIndex(
                name: "IX_magic_login_token_expires_at",
                table: "magic_login_token",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_magic_login_token_requested_email_hash_created_at",
                table: "magic_login_token",
                columns: new[] { "requested_email_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_magic_login_token_requested_ip_created_at",
                table: "magic_login_token",
                columns: new[] { "requested_ip", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_magic_login_token_token_hash",
                table: "magic_login_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_account_organization_organization_id",
                table: "account",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_account_organization_organization_id",
                table: "account");

            migrationBuilder.DropTable(
                name: "account_organization");

            migrationBuilder.DropTable(
                name: "magic_login_token");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_account_subject",
                table: "account");

            migrationBuilder.DropIndex(
                name: "IX_account_organization_id_email",
                table: "account");

            migrationBuilder.DropColumn(
                name: "subject_type",
                table: "client");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "account");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "account");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "account");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "account");

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "account",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<string>(
                name: "password",
                table: "account",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
