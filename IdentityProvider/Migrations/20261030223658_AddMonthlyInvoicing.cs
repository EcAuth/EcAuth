using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_invoice",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    year_month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    account_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    tenant_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    stripe_customer_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    stripe_invoice_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    subtotal_jpy = table.Column<long>(type: "bigint", nullable: false),
                    discount_jpy = table.Column<long>(type: "bigint", nullable: false),
                    total_jpy = table.Column<long>(type: "bigint", nullable: false),
                    tax_jpy = table.Column<long>(type: "bigint", nullable: true),
                    stripe_total_jpy = table.Column<long>(type: "bigint", nullable: true),
                    amount_paid_jpy = table.Column<long>(type: "bigint", nullable: true),
                    finalizes_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    stripe_checked_ticks = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_invoice", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_invoice_run",
                columns: table => new
                {
                    year_month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    failed = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_invoice_run", x => x.year_month);
                });

            migrationBuilder.CreateTable(
                name: "billing_invoice_line",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    billing_invoice_id = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    organization_id = table.Column<int>(type: "int", nullable: true),
                    organization_code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    organization_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    client_db_id = table.Column<int>(type: "int", nullable: true),
                    client_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    app_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    subject_type = table.Column<int>(type: "int", nullable: true),
                    monthly_active_users = table.Column<int>(type: "int", nullable: false),
                    free_tier_mau = table.Column<int>(type: "int", nullable: false),
                    billable_units = table.Column<int>(type: "int", nullable: false),
                    list_price_jpy = table.Column<long>(type: "bigint", nullable: false),
                    amount_jpy = table.Column<long>(type: "bigint", nullable: false),
                    exempt_reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    stripe_invoice_item_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_invoice_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_billing_invoice_line_billing_invoice_billing_invoice_id",
                        column: x => x.billing_invoice_id,
                        principalTable: "billing_invoice",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_invoice_stripe_invoice_id",
                table: "billing_invoice",
                column: "stripe_invoice_id",
                unique: true,
                filter: "[stripe_invoice_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_billing_invoice_year_month_account_subject",
                table: "billing_invoice",
                columns: new[] { "year_month", "account_subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_invoice_line_billing_invoice_id",
                table: "billing_invoice_line",
                column: "billing_invoice_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_invoice_line");

            migrationBuilder.DropTable(
                name: "billing_invoice_run");

            migrationBuilder.DropTable(
                name: "billing_invoice");
        }
    }
}
