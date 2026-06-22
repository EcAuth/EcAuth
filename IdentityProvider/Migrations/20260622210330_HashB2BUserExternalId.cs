using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityProvider.Migrations
{
    /// <inheritdoc />
    public partial class HashB2BUserExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // b2b_user.external_id を平文から正規化 + SHA-256 ハッシュへ移行する（個人情報非保持要件 3.2.1）。
            //
            // C# 側の IdpUtilities.EmailHashUtil.HashEmail と完全に同一のハッシュ値を生成する必要がある:
            //   normalized = email.Trim().ToLowerInvariant()
            //   hash       = Convert.ToHexString(SHA256(UTF8(normalized)))   // 大文字 hex 64 文字
            // これを T-SQL で再現する:
            //   - 正規化:   LOWER(LTRIM(RTRIM(external_id)))
            //   - UTF-8:    CONVERT(varchar, ... COLLATE Latin1_General_100_CI_AS_SC_UTF8) で UTF-8 バイト列にする
            //               （nvarchar のまま HASHBYTES に渡すと UTF-16LE バイトになり C# と一致しないため必須）
            //   - SHA-256:  HASHBYTES('SHA2_256', ...)
            //   - 大文字hex: CONVERT(varchar(64), ..., 2)  // style 2 = 0x なし大文字 hex（Convert.ToHexString と一致）
            //
            // 対象行の限定:
            //   - external_id <> ''                                  : 過去マイグレーションのプレースホルダ空文字は対象外
            //                                                          （EmailHashUtil は空白で例外を投げるため）
            //   - LEN <> 64 OR external_id LIKE '%[^0-9A-F]%'        : 既にハッシュ済み（64 桁 hex）の行を除外し冪等化
            //
            // 注意: CLAUDE.md のマイグレーション設計ルールに従い、external_id を参照する UPDATE は
            //       EXEC() でラップして名前解決を実行時まで遅延させ、sys.columns で列存在を確認する。
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.b2b_user')
                           AND name = 'external_id')
                BEGIN
                    EXEC('
                        UPDATE dbo.b2b_user
                        SET external_id = CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                            CONVERT(varchar(1020),
                                    LOWER(LTRIM(RTRIM(external_id))) COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2)
                        WHERE external_id <> ''''
                          AND (LEN(external_id) <> 64 OR external_id LIKE ''%[^0-9A-F]%'')
                    ');
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // この移行は不可逆。external_id はハッシュ化されており、平文（login_id / メールアドレス）は
            // 復元できないため、データ面のロールバックは行わない（no-op）。
            // ロールバックが必要な場合は、移行前に取得したバックアップから b2b_user を復元すること。
        }
    }
}
