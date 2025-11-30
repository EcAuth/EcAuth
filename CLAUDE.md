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
