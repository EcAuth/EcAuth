using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexToClientClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "varchar(512)",
                unicode: false,
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_client_client_id",
                table: "client",
                column: "client_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_client_client_id",
                table: "client");

            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(512)",
                oldUnicode: false,
                oldMaxLength: 512);
        }
    }
}
