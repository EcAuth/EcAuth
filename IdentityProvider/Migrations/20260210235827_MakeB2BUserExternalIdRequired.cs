using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class MakeB2BUserExternalIdRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user");

            migrationBuilder.AlterColumn<string>(
                name: "external_id",
                table: "b2b_user",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user",
                columns: new[] { "organization_id", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user");

            migrationBuilder.AlterColumn<string>(
                name: "external_id",
                table: "b2b_user",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user",
                columns: new[] { "organization_id", "external_id" },
                unique: true,
                filter: "[external_id] IS NOT NULL");
        }
    }
}
