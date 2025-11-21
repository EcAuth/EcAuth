using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MockOpenIdProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "refresh_token",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "mock_idp_user",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "client",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "authorization_code",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "organization_id",
                table: "access_token",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "organization",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    tenant_name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization", x => x.id);
                });

            // 初期Organization データ投入（dev, staging, production）
            migrationBuilder.Sql(@"
                INSERT INTO organization (name, tenant_name, created_at, updated_at)
                VALUES
                    ('Development', 'dev', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                    ('Staging', 'staging', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                    ('Production', 'production', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
            ");

            // 既存レコードに organization_id を設定（デフォルトは dev）
            migrationBuilder.Sql(@"
                DECLARE @devOrgId INT
                SELECT @devOrgId = id FROM organization WHERE tenant_name = 'dev'

                UPDATE client SET organization_id = @devOrgId WHERE organization_id = 0
                UPDATE mock_idp_user SET organization_id = @devOrgId WHERE organization_id = 0
                UPDATE authorization_code SET organization_id = @devOrgId WHERE organization_id = 0
                UPDATE access_token SET organization_id = @devOrgId WHERE organization_id = 0
                UPDATE refresh_token SET organization_id = @devOrgId WHERE organization_id = 0
            ");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_token_organization_id",
                table: "refresh_token",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_mock_idp_user_organization_id",
                table: "mock_idp_user",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_client_organization_id_client_id",
                table: "client",
                columns: new[] { "organization_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_authorization_code_organization_id",
                table: "authorization_code",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_access_token_organization_id",
                table: "access_token",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_tenant_name",
                table: "organization",
                column: "tenant_name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_access_token_organization_organization_id",
                table: "access_token",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_organization_organization_id",
                table: "authorization_code",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_client_organization_organization_id",
                table: "client",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_mock_idp_user_organization_organization_id",
                table: "mock_idp_user",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refresh_token_organization_organization_id",
                table: "refresh_token",
                column: "organization_id",
                principalTable: "organization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_access_token_organization_organization_id",
                table: "access_token");

            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_organization_organization_id",
                table: "authorization_code");

            migrationBuilder.DropForeignKey(
                name: "FK_client_organization_organization_id",
                table: "client");

            migrationBuilder.DropForeignKey(
                name: "FK_mock_idp_user_organization_organization_id",
                table: "mock_idp_user");

            migrationBuilder.DropForeignKey(
                name: "FK_refresh_token_organization_organization_id",
                table: "refresh_token");

            migrationBuilder.DropTable(
                name: "organization");

            migrationBuilder.DropIndex(
                name: "IX_refresh_token_organization_id",
                table: "refresh_token");

            migrationBuilder.DropIndex(
                name: "IX_mock_idp_user_organization_id",
                table: "mock_idp_user");

            migrationBuilder.DropIndex(
                name: "IX_client_organization_id_client_id",
                table: "client");

            migrationBuilder.DropIndex(
                name: "IX_authorization_code_organization_id",
                table: "authorization_code");

            migrationBuilder.DropIndex(
                name: "IX_access_token_organization_id",
                table: "access_token");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "refresh_token");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "mock_idp_user");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "client");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "access_token");

            migrationBuilder.AlterColumn<string>(
                name: "client_id",
                table: "client",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
