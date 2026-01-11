using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class AddB2BPasskeyEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_rp_ids",
                table: "client",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "b2b_user",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    user_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    organization_id = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_b2b_user", x => x.id);
                    table.UniqueConstraint("AK_b2b_user_subject", x => x.subject);
                    table.ForeignKey(
                        name: "FK_b2b_user_organization_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webauthn_challenge",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    challenge = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    session_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    user_type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    rp_id = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    client_id = table.Column<int>(type: "int", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webauthn_challenge", x => x.id);
                    table.ForeignKey(
                        name: "FK_webauthn_challenge_client_client_id",
                        column: x => x.client_id,
                        principalTable: "client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "b2b_passkey_credential",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    b2b_subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    credential_id = table.Column<byte[]>(type: "varbinary(900)", nullable: false),
                    public_key = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    device_name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    aa_guid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    transports = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_b2b_passkey_credential", x => x.id);
                    table.ForeignKey(
                        name: "FK_b2b_passkey_credential_b2b_user_b2b_subject",
                        column: x => x.b2b_subject,
                        principalTable: "b2b_user",
                        principalColumn: "subject",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_b2b_passkey_credential_b2b_subject",
                table: "b2b_passkey_credential",
                column: "b2b_subject");

            migrationBuilder.CreateIndex(
                name: "IX_b2b_passkey_credential_credential_id",
                table: "b2b_passkey_credential",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_b2b_user_organization_id_external_id",
                table: "b2b_user",
                columns: new[] { "organization_id", "external_id" },
                unique: true,
                filter: "[external_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_challenge_client_id",
                table: "webauthn_challenge",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_challenge_expires_at",
                table: "webauthn_challenge",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_challenge_session_id",
                table: "webauthn_challenge",
                column: "session_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "b2b_passkey_credential");

            migrationBuilder.DropTable(
                name: "webauthn_challenge");

            migrationBuilder.DropTable(
                name: "b2b_user");

            migrationBuilder.DropColumn(
                name: "allowed_rp_ids",
                table: "client");
        }
    }
}
