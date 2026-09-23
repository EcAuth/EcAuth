using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyActiveUserTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monthly_active_user",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    year_month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    client_id = table.Column<int>(type: "int", nullable: false),
                    organization_id = table.Column<int>(type: "int", nullable: false),
                    subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    subject_type = table.Column<int>(type: "int", nullable: false),
                    auth_method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_active_user", x => x.id);
                    table.ForeignKey(
                        name: "FK_monthly_active_user_client_client_id",
                        column: x => x.client_id,
                        principalTable: "client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monthly_active_user_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monthly_active_user_client_id",
                table: "monthly_active_user",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_monthly_active_user_organization_id",
                table: "monthly_active_user",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_monthly_active_user_year_month_client_id_subject",
                table: "monthly_active_user",
                columns: new[] { "year_month", "client_id", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monthly_active_user_year_month_organization_id",
                table: "monthly_active_user",
                columns: new[] { "year_month", "organization_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monthly_active_user");
        }
    }
}
