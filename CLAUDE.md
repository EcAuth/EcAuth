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
cd E2ETests && pnpm install && pnpm exec playwright test
```

## 注意事項

- 日本語で回答してください
- docs/ は EcAuthDocs リポジトリを clone したものです
- 起動時に docs/ の内容を最新の main ブランチに更新してください

### 環境変数の配線ルール

環境変数の値は 1Password で一元管理されているが、1Password から各ランタイムへの **配線（マッピング）** は以下の4箇所に分散している。新しい環境変数を追加・変更する場合は、**必ず全箇所を確認**すること。

| 配線先 | ファイル | 用途 |
|--------|----------|------|
| ローカル開発 | `.env.dev.tpl`, `.env.staging.tpl` | `op run --env-file=...` でサブプロセスに注入（平文 `.env` は生成しない） |
| CI（Staging） | `.github/workflows/staging.yml` | `1password/load-secrets-action` で CI 環境変数に展開 |
| CI（Production） | `.github/workflows/production.yml` | 同上 |
| Azure ランタイム | `ecauth-infrastructure/environments/staging/main.tf` | Terraform `onepassword_item` → `app_settings` |

**特に注意が必要なケース:**
- DbInitializer / シーダーが参照する環境変数は **Azure ランタイム（Terraform `app_settings`）** に設定が必要。CI ワークフローに定義があっても、Terraform に漏れているとアプリ起動時にシーダーがスキップされる
- B2BPasskeySeeder は DEV 環境では `DEFAULT_*` を、Staging/Production では `STAGING_*` / `PROD_*` プレフィックスの変数を参照する分岐がある。シーダーのコード（`B2BPasskeySeeder.cs`）で実際に参照される全変数名を確認すること

**配線先は「消費箇所」で判断する（4箇所すべてに一律で入れない）:**

> ⚠️ レビュー bot（CodeRabbit / claude-review / github-actions）は「新規環境変数を 4 箇所すべてに追加せよ」と機械的に指摘しがちだが、**配線先はその変数がどこで消費されるかで決まる**。以下の区別に従うこと（この区別は設計判断であり、未追跡の欠落ではない）。

- **CI ステップ（マイグレーション / デプロイ / 起動時シード）が参照する値** → CI ワークフロー（`staging.yml` / `production.yml`）にも配線する。
  - 例: `ACCOUNTS_*` / `STG_ACCOUNTS_*` / `SENDGRID_API_KEY`（`production.yml` に存在。コメント「実際のランタイム注入は Terraform app_settings。ここは CI 配線」を参照）。
- **アプリのリクエスト処理時のみ消費される非秘密の設定値** → CI ワークフローには**入れない**。`.env.dev.tpl`（ローカル）+ **Azure ランタイム（Terraform `app_settings`）** のみに配線する。CI はランタイム `app_settings` を運ばないため。
  - 例: `Signup:ConfirmBaseUrl:{tenant}` / `MagicLink:BaseUrl:{tenant}`（`BuildConfirmUrl` / `BuildMagicLinkUrl` がリクエスト時に参照。`.yml` には無いのが正しい）。
- **機能が動作する環境にのみ配線する**。例: `accounts` / `stg-accounts`（Account 申込・マジックリンク）機能は**本番 App Service のみ**で動く（staging は F プランで accounts org をシードしない）。よって配線先は `environments/production/main.tf` であって `environments/staging/main.tf` ではなく、staging の `.env.staging.tpl` / `staging.yml` にも入れない。

### Application Insights 上のステップ別プロファイリング

`/token` `/userinfo` `register/verify` `authenticate/verify` の各エンドポイントは、`IdentityProvider.Telemetry.TimingScope` を使った `using` ブロックで処理ステップ毎の所要時間を `Activity.Current` のタグとして記録している。Azure Monitor が `Activity` タグを自動的に `customDimensions` にマッピングするため、本番テレメトリ上で内訳をクエリできる。

タグキーは `step.{step_name}.elapsed_ms`。値はミリ秒単位の文字列（`InvariantCulture` の `F3` フォーマット、例: `"12.345"`）。Azure Monitor の OpenTelemetry エクスポーターは数値型の Activity タグを customDimensions に出力しないため、SDK 側で文字列化してから `SetTag` する。クエリ側は `todouble(customDimensions["..."])` で数値として扱う。

#### 計測対象（2026-04 時点）

| エンドポイント | ステップ |
|---|---|
| `/token` | `client_lookup` / `auth_code_lookup` / `auth_code_mark_used` / `user_lookup` / `token_generate` |
| `/userinfo` | `auth_header_parse` / `access_token_validate` / `user_lookup` |
| `/api/external-userinfo` | `auth_header_parse` / `access_token_validate` / `external_userinfo_fetch` |
| `register/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `fido2_make_credential` / `credential_persist` / `challenge_consume`） |
| `authenticate/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `credential_lookup` / `fido2_make_assertion` / `signcount_persist` / `challenge_consume`） |
| `/api/signup/request` | `validate` / `persist` / `send_email` |
| `/api/signup/confirm` | `token_lookup` / `confirm` |
| `/api/signup/status` | `status_lookup` |
| `/api/account/magic-link/request` | `rate_limit` / `account_lookup` / `persist` / `send_email` |
| `/api/account/magic-link/verify` | `token_lookup` / `token_consume` |

#### Application Insights クエリ例

`/token` の各ステップの p50 / p95 を分解:

```kusto
requests
| where url has "/v1/token" and timestamp > ago(7d)
| extend
    step_client_lookup = todouble(customDimensions["step.client_lookup.elapsed_ms"]),
    step_auth_code_lookup = todouble(customDimensions["step.auth_code_lookup.elapsed_ms"]),
    step_auth_code_mark_used = todouble(customDimensions["step.auth_code_mark_used.elapsed_ms"]),
    step_user_lookup = todouble(customDimensions["step.user_lookup.elapsed_ms"]),
    step_token_generate = todouble(customDimensions["step.token_generate.elapsed_ms"])
| summarize
    p50_total = percentile(duration, 50),
    p95_total = percentile(duration, 95),
    p50_client = percentile(step_client_lookup, 50),
    p50_auth_code = percentile(step_auth_code_lookup, 50),
    p50_user = percentile(step_user_lookup, 50),
    p50_token = percentile(step_token_generate, 50)
```

`authenticate/verify` の Fido2 検証本体（`fido2_make_assertion`）が主因かを確認:

```kusto
requests
| where url has "authenticate/verify" and timestamp > ago(7d)
| extend
    step_fido2 = todouble(customDimensions["step.fido2_make_assertion.elapsed_ms"]),
    step_lookup = todouble(customDimensions["step.credential_lookup.elapsed_ms"])
| summarize
    p50_total = percentile(duration, 50),
    p50_fido2 = percentile(step_fido2, 50),
    p50_lookup = percentile(step_lookup, 50)
| extend
    fido2_share = round(p50_fido2 / p50_total * 100, 1)
```

#### 計測ポイントの追加方法

```csharp
using IdentityProvider.Telemetry;

using (TimingScope.Begin("my_step"))
{
    await SomeAsyncWork();
}
```

- `Activity.Current` が null（ローカル開発で Application Insights 未設定など）の場合は no-op
- ネスト可、各スコープが独立して `step.{name}.elapsed_ms` タグを付与

### マイグレーション設計ルール

- `migrationBuilder.Sql()` でカラムを参照する UPDATE/INSERT 文を書く場合、`EXEC()` 動的 SQL でラップすること
  - CI/CD の `dotnet ef migrations script --idempotent` で生成されるスクリプトは全マイグレーションが 1 バッチにまとめられる
  - SQL Server はバッチ全体をコンパイル時に検証するため、同一マイグレーション内で追加したカラムを参照する DML はコンパイルエラーになる
  - `EXEC()` でラップすることで名前解決を実行時まで遅延させる
- 破壊的変更（カラム削除・リネーム）を伴うマイグレーションのデータ移行 SQL には `IF EXISTS` でカラム存在チェックを追加すること
  - これにより、関連するカラムを削除する後続のマイグレーションが既に適用されている環境でも、冪等スクリプトがエラーなく実行できるようになります。
- カラム存在チェックには `INFORMATION_SCHEMA.COLUMNS` ではなく `sys.columns` + `OBJECT_ID(N'dbo.table_name')` を使用し、DML でもスキーマ修飾（`dbo.`）を明示すること
