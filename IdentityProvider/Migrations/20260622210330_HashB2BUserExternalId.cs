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
            //   - 正規化:   LOWER(LTRIM(RTRIM(external_id)) COLLATE Latin1_General_100_CS_AS_SC)
            //               ※ LOWER は照合順序依存のため、引数側に Latin1 の大文字小文字マッピングを持つ照合順序
            //                  （CS_AS_SC）を明示し、DB デフォルト照合順序に依存しない invariant 相当の小文字化にする。
            //                  DB デフォルトのまま LOWER を使うと、例えば Turkish 照合では 'I' → 'ı' となり
            //                  ToLowerInvariant の 'i' と乖離する。CS_AS_SC は 'I' → 'i' と正しく小文字化する
            //                  （実 SQL Server 2022 / Turkish 既定照合 DB で C# EmailHashUtil との一致を確認済み）。
            //   - UTF-8:    CONVERT(varchar, ... COLLATE Latin1_General_100_CI_AS_SC_UTF8) で UTF-8 バイト列にする
            //               （nvarchar のまま HASHBYTES に渡すと UTF-16LE バイトになり C# と一致しないため必須）
            //   - SHA-256:  HASHBYTES('SHA2_256', ...)
            //   - 大文字hex: CONVERT(varchar(64), ..., 2)  // style 2 = 0x なし大文字 hex（Convert.ToHexString と一致）
            //
            // 対象行: external_id <> '' の全行（空文字は過去マイグレーションのプレースホルダのため対象外。
            //         EmailHashUtil も空白で例外を投げる）。値の形状によるスキップは行わない（ガード3 参照）。
            //         冪等性は EF のマイグレーション履歴（__EFMigrationsHistory）により担保される（Up は一度だけ実行）。
            //
            // 移行前ガード（不可逆かつログイン不能/移行失敗を避けるため、異常データを検出したら THROW で停止）:
            //   ガード1: C# String.Trim() は Char.IsWhiteSpace が真の全 Unicode 空白を除去するが、T-SQL LTRIM/RTRIM は
            //            U+0020（ASCII スペース）のみを除去する。先頭/末尾にそれ以外の空白を含む external_id があると
            //            C# 経由のハッシュと一致せず移行後にログイン不能（不可逆）になるため停止する。
            //            （U+0020 は LTRIM/RTRIM で C# と同様に除去されるため検出対象外）
            //   ガード2: LOWER の正規化で同一 organization_id 内の複数 external_id が同一ハッシュに収束すると
            //            (organization_id, external_id) ユニーク制約違反で移行が途中失敗するため、事前に検出して停止する。
            //   ガード3: 既に 64 桁 hex 形状の external_id を検出して停止する。これは (a) 偶然 64 桁 hex の平文 login_id
            //            （値の形状だけでは適用済みハッシュと区別不能）、(b) 部分適用/手動再実行で一部だけハッシュ済みの
            //            状態、のいずれかを示す。誤って二重ハッシュ/スキップせず、手動確認を促すために停止する。
            //
            // 注意: CLAUDE.md のルールに従い、external_id を参照する文は EXEC() でラップして名前解決を
            //       実行時まで遅延させる（idempotent script では全マイグレーションが 1 バッチでコンパイルされ、
            //       新規 DB では external_id 列がコンパイル時点で存在しないため）。sys.columns で列存在も確認する。
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'dbo.b2b_user')
                           AND name = 'external_id')
                BEGIN
                    -- ガード1: 先頭/末尾の非スペース空白（Char.IsWhiteSpace 全体、U+0020 を除く）を検出
                    EXEC('
                        DECLARE @ws TABLE (c nchar(1));
                        INSERT INTO @ws (c) VALUES
                            (NCHAR(9)), (NCHAR(10)), (NCHAR(11)), (NCHAR(12)), (NCHAR(13)), (NCHAR(133)), (NCHAR(160)),
                            (NCHAR(5760)), (NCHAR(8192)), (NCHAR(8193)), (NCHAR(8194)), (NCHAR(8195)), (NCHAR(8196)),
                            (NCHAR(8197)), (NCHAR(8198)), (NCHAR(8199)), (NCHAR(8200)), (NCHAR(8201)), (NCHAR(8202)),
                            (NCHAR(8232)), (NCHAR(8233)), (NCHAR(8239)), (NCHAR(8287)), (NCHAR(12288));
                        IF EXISTS (
                            SELECT 1 FROM dbo.b2b_user u
                            WHERE u.external_id <> ''''
                              AND EXISTS (
                                    SELECT 1 FROM @ws w
                                    WHERE LEFT(u.external_id, 1)  = w.c COLLATE Latin1_General_100_BIN2
                                       OR RIGHT(u.external_id, 1) = w.c COLLATE Latin1_General_100_BIN2
                              )
                        )
                            THROW 50001, ''HashB2BUserExternalId: external_id with leading/trailing non-space whitespace (Char.IsWhiteSpace) detected. C# Trim() would not match this SQL hash. Clean the data before re-running.'', 1;
                    ');

                    -- ガード3: 既に 64 桁 hex 形状の external_id を検出（二重ハッシュ/平文取りこぼし防止）
                    EXEC('
                        IF EXISTS (
                            SELECT 1 FROM dbo.b2b_user
                            WHERE external_id <> ''''
                              AND LEN(external_id) = 64
                              AND external_id NOT LIKE ''%[^0-9A-Fa-f]%''
                        )
                            THROW 50003, ''HashB2BUserExternalId: external_id that already looks like a 64-char hex hash detected. Cannot distinguish a 64-hex plaintext login_id from an already-applied hash; verify/resolve the data before re-running.'', 1;
                    ');

                    -- ガード2: 正規化後ハッシュの同一 organization_id 内での衝突を検出
                    EXEC('
                        IF EXISTS (
                            SELECT 1 FROM (
                                SELECT organization_id,
                                       CONVERT(varchar(64), HASHBYTES(''SHA2_256'',
                                           CONVERT(varchar(1020),
                                                   LOWER(LTRIM(RTRIM(external_id)) COLLATE Latin1_General_100_CS_AS_SC) COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2) AS h
                                FROM dbo.b2b_user
                                WHERE external_id <> ''''
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
                                    LOWER(LTRIM(RTRIM(external_id)) COLLATE Latin1_General_100_CS_AS_SC) COLLATE Latin1_General_100_CI_AS_SC_UTF8)), 2)
                        WHERE external_id <> ''''
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
