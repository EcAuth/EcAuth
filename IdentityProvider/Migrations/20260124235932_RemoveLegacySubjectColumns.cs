using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <summary>
    /// 旧カラム（EcAuthSubject/B2BSubject）を削除するマイグレーション
    ///
    /// 【重要】このマイグレーションは、既存データの移行が完了した後に適用してください。
    /// 適用前に以下を確認してください：
    /// 1. 全ての access_token レコードに subject と subject_type が設定されている
    /// 2. 全ての authorization_code レコードに subject_new と subject_type が設定されている
    /// 3. アプリケーションコードが新しいカラム（Subject/SubjectType）のみを使用している
    ///
    /// ロールバックが可能なように Down メソッドが実装されていますが、
    /// 削除されたカラムのデータは復元されません。
    /// </summary>
    /// <inheritdoc />
    public partial class RemoveLegacySubjectColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_access_token_ecauth_user_ecauth_subject",
                table: "access_token");

            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_b2b_user_b2b_subject",
                table: "authorization_code");

            migrationBuilder.DropForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code");

            migrationBuilder.DropIndex(
                name: "IX_authorization_code_b2b_subject",
                table: "authorization_code");

            migrationBuilder.DropIndex(
                name: "IX_authorization_code_ecauth_subject",
                table: "authorization_code");

            migrationBuilder.DropIndex(
                name: "IX_access_token_ecauth_subject",
                table: "access_token");

            migrationBuilder.DropColumn(
                name: "b2b_subject",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "ecauth_subject",
                table: "authorization_code");

            migrationBuilder.DropColumn(
                name: "ecauth_subject",
                table: "access_token");

            migrationBuilder.RenameColumn(
                name: "subject_new",
                table: "authorization_code",
                newName: "subject");

            migrationBuilder.AlterColumn<int>(
                name: "subject_type",
                table: "authorization_code",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "subject_type",
                table: "access_token",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "subject",
                table: "access_token",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "subject",
                table: "authorization_code",
                newName: "subject_new");

            migrationBuilder.AlterColumn<int>(
                name: "subject_type",
                table: "authorization_code",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "subject_new",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<string>(
                name: "b2b_subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ecauth_subject",
                table: "authorization_code",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "subject_type",
                table: "access_token",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "subject",
                table: "access_token",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<string>(
                name: "ecauth_subject",
                table: "access_token",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_code_b2b_subject",
                table: "authorization_code",
                column: "b2b_subject");

            migrationBuilder.CreateIndex(
                name: "IX_authorization_code_ecauth_subject",
                table: "authorization_code",
                column: "ecauth_subject");

            migrationBuilder.CreateIndex(
                name: "IX_access_token_ecauth_subject",
                table: "access_token",
                column: "ecauth_subject");

            migrationBuilder.AddForeignKey(
                name: "FK_access_token_ecauth_user_ecauth_subject",
                table: "access_token",
                column: "ecauth_subject",
                principalTable: "ecauth_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_b2b_user_b2b_subject",
                table: "authorization_code",
                column: "b2b_subject",
                principalTable: "b2b_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_authorization_code_ecauth_user_ecauth_subject",
                table: "authorization_code",
                column: "ecauth_subject",
                principalTable: "ecauth_user",
                principalColumn: "subject",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
