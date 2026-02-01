# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

詳細なアーキテクチャ、開発ガイドライン、E2E テスト方法は @docs/CLAUDE.md を参照してください。

## 概要

OpenID Connect の ID フェデレーションに特化した Identity Provider システムです。

## 開発コマンド

```bash
# ビルド
dotnet build EcAuth.sln

# テスト実行
dotnet test IdentityProvider.Test/IdentityProvider.Test.csproj

# E2Eテスト（E2ETestsディレクトリで）
cd E2ETests && yarn install && npx playwright test
```

## 注意事項

- 日本語で回答してください
- docs/ は EcAuthDocs リポジトリを clone したものです
- 起動時に docs/ の内容を最新の main ブランチに更新してください

### マイグレーション設計ルール

- `migrationBuilder.Sql()` でカラムを参照する UPDATE/INSERT 文を書く場合、`EXEC()` 動的 SQL でラップすること
  - CI/CD の `dotnet ef migrations script --idempotent` で生成されるスクリプトは全マイグレーションが 1 バッチにまとめられる
  - SQL Server はバッチ全体をコンパイル時に検証するため、同一マイグレーション内で追加したカラムを参照する DML はコンパイルエラーになる
  - `EXEC()` でラップすることで名前解決を実行時まで遅延させる
- 破壊的変更（カラム削除・リネーム）を伴うマイグレーションのデータ移行 SQL には `IF EXISTS` でカラム存在チェックを追加すること
  - これにより、関連するカラムを削除する後続のマイグレーションが既に適用されている環境でも、冪等スクリプトがエラーなく実行できるようになります。
