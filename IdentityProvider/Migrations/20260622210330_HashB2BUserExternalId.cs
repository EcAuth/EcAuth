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
            //   - LEN <> 64 OR external_id LIKE '%[^0-9A-Fa-f]%'     : 既にハッシュ済み（64 桁 hex）の行を除外し冪等化。
            //                                                          大文字小文字双方を許容し DB の照合順序（CS/CI）に依存しない。
            //
            // 移行前ガード（不可逆かつログイン不能/移行失敗を避けるため、異常データを検出したら THROW で停止）:
            //   ガード1: C# String.Trim() は全 Unicode 空白を除去するが、T-SQL LTRIM/RTRIM は ASCII スペースのみ。
            //            先頭/末尾に非スペース空白（TAB/LF/VT/FF/CR/NEL/NBSP/全角スペース）を含む external_id が
            //            あると C# 経由のハッシュと一致せず、移行後にログイン不能（不可逆）になるため停止する。
            //   ガード2: LOWER/LTRIM/RTRIM の正規化で、同一 organization_id 内の複数 external_id が同一ハッシュに
            //            収束すると (organization_id, external_id) ユニーク制約違反で移行が途中失敗するため、
            //            事前に衝突を検出して停止する。
            //
            // 注意: CLAUDE.md のルールに従い、external_id を参照する文は EXEC() でラップして名前解決を
            //       実行時まで遅延させる（idempotent script では全マイグレーションが 1 バッチでコンパイルされ、
            //       新規 DB では external_id 列がコンパイル時点で存在しないため）。sys.columns で列存在も確認する。
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.b2b_user')
                           AND name = 'external_id')
                BEGIN
                    -- ガード1: 先頭/末尾の非スペース空白を検出
                    EXEC('
                        IF EXISTS (
                            SELECT 1 FROM dbo.b2b_user
                            WHERE external_id <> ''''
                              AND (LEFT(external_id, 1)  IN (NCHAR(9), NCHAR(10), NCHAR(11), NCHAR(12), NCHAR(13), NCHAR(133), NCHAR(160), NCHAR(12288))
                                OR RIGHT(external_id, 1) IN (NCHAR(9), NCHAR(10), NCHAR(11), NCHAR(12), NCHAR(13), NCHAR(133), NCHAR(160), NCHAR(12288)))
                        )
                            THROW 50001, ''HashB2BUserExternalId: external_id with leading/trailing non-space whitespace (TAB/LF/CR/NBSP/ideographic space) detected. C# Trim() would not match this SQL hash. Clean the data before re-running.'', 1;
                    ');

                    -- ガード2: 正規化後ハッシュの同一 organization_id 内での衝突を検出
                    EXEC('
                        IF EXISTS (
                            SELECT 1 FROM (
                                SELECT organization_id,
                                       CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                                           CONVERT(varchar(1020),
                                                   LOWER(LTRIM(RTRIM(external_id))) COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2) AS h
                                FROM dbo.b2b_user
                                WHERE external_id <> ''''
                                  AND (LEN(external_id) <> 64 OR external_id LIKE ''%[^0-9A-Fa-f]%'')
                            ) s
                            GROUP BY organization_id, h
                            HAVING COUNT(*) > 1
                        )
                            THROW 50002, ''HashB2BUserExternalId: normalized external_id hashes collide within an organization (case/whitespace-different duplicates). Resolve the data before re-running.'', 1;
                    ');

                    -- 本処理: 平文を正規化 + SHA-256 ハッシュへ移行
                    EXEC('
                        UPDATE dbo.b2b_user
                        SET external_id = CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                            CONVERT(varchar(1020),
                                    LOWER(LTRIM(RTRIM(external_id))) COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2)
                        WHERE external_id <> ''''
                          AND (LEN(external_id) <> 64 OR external_id LIKE ''%[^0-9A-Fa-f]%'')
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
