using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingCustomerAndPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "billing_exempt",
                table: "client",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "billing_exempt_reason",
                table: "client",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payment_method_registered_at",
                table: "account",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_customer_id",
                table: "account",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_billing_plan",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    account_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    billing_exempt = table.Column<bool>(type: "bit", nullable: false),
                    exempt_reason = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    discount_percent = table.Column<int>(type: "int", nullable: true),
                    discount_jpy = table.Column<long>(type: "bigint", nullable: true),
                    b2b_tiers_json = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    b2c_tiers_json = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    valid_until = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_billing_plan", x => x.id);
                    table.ForeignKey(
                        name: "FK_account_billing_plan_account_account_subject",
                        column: x => x.account_subject,
                        principalTable: "account",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stripe_webhook_event",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    tenant_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_webhook_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_stripe_customer_id",
                table: "account",
                column: "stripe_customer_id",
                unique: true,
                filter: "[stripe_customer_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_account_billing_plan_account_subject",
                table: "account_billing_plan",
                column: "account_subject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_billing_plan");

            migrationBuilder.DropTable(
                name: "stripe_webhook_event");

            migrationBuilder.DropIndex(
                name: "IX_account_stripe_customer_id",
                table: "account");

            migrationBuilder.DropColumn(
                name: "billing_exempt",
                table: "client");

            migrationBuilder.DropColumn(
                name: "billing_exempt_reason",
                table: "client");

            migrationBuilder.DropColumn(
                name: "payment_method_registered_at",
                table: "account");

            migrationBuilder.DropColumn(
                name: "stripe_customer_id",
                table: "account");
        }
    }
}
