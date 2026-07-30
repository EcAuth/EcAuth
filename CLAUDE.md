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
| `/token` | `client_lookup` / `client_secret_verify` / `auth_code_lookup` / `auth_code_mark_used` / `user_lookup` / `token_generate` |
| `/userinfo` | `auth_header_parse` / `access_token_validate` / `user_lookup` |
| `/api/external-userinfo` | `auth_header_parse` / `access_token_validate` / `external_userinfo_fetch` |
| `register/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `fido2_make_credential` / `credential_persist` / `challenge_consume`） |
| `authenticate/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `credential_lookup` / `fido2_make_assertion` / `signcount_persist` / `challenge_consume`） |
| `/api/signup/request` | `validate` / `persist` / `send_email` |
| `/api/signup/confirm` | `token_lookup` / `confirm`（内訳: `client_secret_protect`） |
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

#### 起動時（`app.Run()` 前）のログは App Insights ではなく `AppServiceConsoleLogs` を見る

**重要**: `DbInitializer` / 各 `Seeder`（`OrganizationClientSeeder` の B2C→B2B 補正など）の起動ログは
**App Insights の `traces` / `AppTraces` には出ない**。App Insights（OpenTelemetry）のログ送信パイプラインは
host start（`app.Run()` 内の `TelemetryHostedService.StartAsync`）で初めて起動するため、それより前に走る
起動コードのログは exporter に届かない（`Microsoft.Hosting.Lifetime` の「Application started」が host start の境界）。

これらの起動ログは **コンテナ stdout（既定の Console ロガー）→ Log Analytics の `AppServiceConsoleLogs`** に出る。
本番 Web App の診断設定（`ecauth-prod-0e9f509a-diag`）が `AppServiceConsoleLogs` / `AppServiceAppLogs` /
`AppServiceHTTPLogs` を ワークスペース `ecauth-prod-0e9f509a-insights-law`（ワークスペースベース App Insights の
バック）へ送る配線を**既に持っている**。したがって起動診断はインフラ変更なしで観測できる。

```kusto
// 起動シーダー / DbInitializer の実行・補正ログ（Log Analytics ワークスペースに対して実行）
AppServiceConsoleLogs
| where TimeGenerated > ago(1d)
| where ResultDescription has "DbInitializer"
     or ResultDescription has "SubjectType を"   // B2C→B2B 補正ログ（補正が発火したときのみ出力）
     or ResultDescription has "Seeder"
| project TimeGenerated, ResultDescription
| order by TimeGenerated asc
```

- 切り分けの原則: 起動・マイグレーション・シーダー等 `app.Run()` 前の診断は **`AppServiceConsoleLogs`**、
  リクエスト処理時（host start 後）の診断は **`traces` / `AppTraces`**。「App Insights に出ない」と感じたら、
  まず参照テーブルの取り違えを疑う（起動前ログを App Insights に押し込む `ForceFlush` 等の小細工は不要・無効）。

### E2E テストの実装上の要点

`E2ETests/` で B2B パスキーや申込フローを扱うときに、毎回ソースから再導出する羽目になる事実をまとめる。

#### プラグインが呼ぶのは `https://{tenant_name}.ec-auth.io`（店舗のホストではない）

EC-CUBE プラグインは、まず `/platform/v1/client-resolve` に `client_id` を投げ、返ってきた
`base_url`（= `https://{tenant_name}.{PlatformApi:BaseDomain}`、`Controllers/Platform/ClientResolveController.cs:60-61`）を
設定値 `ecauth_base_url` として保存し、**以降の API 呼び出しをすべてそこへ送る**
（4 系 `Controller/Admin/ConfigController.php` の `clientResolveService->resolve()`、
2 系 `SC_Helper_EcAuthLogin2.php` の `CLIENT_RESOLVE_PATH`）。

つまり実運用では **1 リクエストに 2 種類のホストが登場する**:

| 役割 | ホスト | 理由 |
|---|---|---|
| ブラウザ（`navigator.credentials.create` / `get`） | 店舗のホスト（= `rp_id`） | WebAuthn が origin と `rp_id` の一致を要求する |
| サーバー（`*/options`・`*/verify` の HTTP 呼び出し） | `{tenant_name}.ec-auth.io` | プラグインが保存した `ecauth_base_url` |

E2E でこれを一体にして「ブラウザから直接 API を叩く」と、`Host` が店舗のホストになって
`TenantMiddleware` が既定テナントに解決してしまい、`WebAuthnChallenge` のグローバルクエリフィルタ
（`Models/EcAuthDbContext.cs`）が顧客 Organization の行に一致せず **`Session not found or expired` (400)** になる。
これは **テストの誤りであってプロダクトの不具合ではない**。`tests/helpers/b2b-passkey.ts` の `B2BContext` が
`api`（テナントの `Host` を付けた `APIRequestContext`）と `page`（`rp_id` と一致する origin）を分けているのはこのため。

#### `redirect_uri` / `rp_id` をテスト側で組み立てない

`authenticate/verify` の `redirect_uri` は登録値と**完全一致**で検証される（`Controllers/B2BPasskeyController.cs`）。
テストで期待値を組み立てると「申込が登録した初期値」と「プラグインが送る値」のズレ（EcAuth#481 の本体）が
検出できない。`GET /v1/account/clients` で取得した登録済みの値をそのまま使うこと。

#### 疑似店舗ホストに `.test` を使う

`.test` は RFC 6761 の予約 TLD で公開解決されず、実在ドメインと衝突しない。本番 DB に残っても
テストデータであることが明確になる。`Middlewares/TenantMiddleware.cs` の `ExtractTenantNameFromHost` が先頭セグメントを
テナント名として扱うのは **3 セグメント以上**のときだけなので、`e2e-{RUN}.test` の 2 セグメントに
保てば既定テナントに解決される。Playwright 側は `playwright.config.ts` の
`--host-resolver-rules` に `MAP *.test 127.0.0.1` を入れて解決させる。

#### `wwwroot/b2b-passkey-test.html` の配信条件

静的ファイル配信は **`app.Environment.IsProduction()` のときだけ**テナント限定になる
（`Program.cs` の `UseWhen` — ホストが 3 セグメント以上かつ先頭が `DEFAULT_ORGANIZATION_TENANT_NAME`）。
本番では `production.ec-auth.io` からしか見えず、顧客テナントのサブドメインでは 404 になる。
Development / Staging では `app.UseStaticFiles()` が無条件に効く。

#### ローカルでの再実行

```bash
op run --env-file=.env.dev.tpl -- docker compose -p ec-auth up -d --build identityprovider
cd E2ETests && pnpm exec playwright test tests/specs/signup_client_b2b_login.spec.ts --reporter=list
```

### EC-CUBE プラグイン結合 E2E

`eccube_plugin_signup_login.spec.ts` は EC-CUBE 4 系 / 2 系を実際に起動し、
**申込 → プラグイン設定 → パスキー登録 → 管理画面ログイン**を通す。API だけで検証する
`signup_client_b2b_login.spec.ts` では守れない「プラグインが実際に送る値」まで突き合わせる。

```bash
./E2ETests/scripts/eccube-e2e.sh up      # ホスト名を採番して compose を起動（op run 経由）
./E2ETests/scripts/eccube-e2e.sh test tests/specs/eccube_plugin_signup_login.spec.ts --reporter=list
./E2ETests/scripts/eccube-e2e.sh down    # ボリュームごと破棄
```

CI は `.github/workflows/eccube_plugin_e2e.yml`（`main.yml` から呼ばれる）。プラグインの ref と
package-api 経由インストールを `workflow_dispatch` の入力で切り替えられる。

#### package-api（検証キー）経由でローカル実行する

オーナーズストアのリリース申請で発行される検証キー（`X-ECCUBE-KEY`）を渡すと、4 系プラグインを
実際の package-api から入れて検証できる。`op run` は env-file に無い変数をシェルからそのまま通すので、
インラインで渡せばよい。

```bash
ECCUBE_AUTHENTICATION_KEY=$(op read 'op://EcAuth/eccube4-ecauth-plugin/eccube_authentication_key') \
  ./E2ETests/scripts/eccube-e2e.sh up
# バージョンを固定する場合は ECAUTH_PLUGIN_VERSION=1.0.1 も併せて渡す（非秘密）
```

> ⚠️ **`ECCUBE_AUTHENTICATION_KEY` を `.env.dev.tpl` に入れてはいけない**（レビュー bot が
> 「配線が漏れている」と指摘しがちだが、意図的に入れていない）。4 系プラグインの
> `docker-entrypoint.sh` は**キーが非空ならインストール元を package-api に切り替える**。
> `.env.dev.tpl` に入れると、この E2E と無関係な通常のローカル起動まで既定のインストール元が
> ワーキングツリーから package-api に変わり、開発中のプラグインを検証できなくなる。
> ec-cube4-ecauth 側でも同じ理由で `.env.tpl` とは別の `.env.verify.tpl` に分離してある。

#### 構成上の決めごと（変更するとき用）

| 事項 | 決め |
|---|---|
| リバースプロキシ | Caddy 1 台で 443 に集約（`docker/e2e/Caddyfile`）。ポート付き URL だと rp_id / redirect_uri が本番と変わるため |
| 証明書 | Caddy の `local_certs`。ルート CA を EC-CUBE の信頼ストアへ注入する（`docker/e2e/eccube-entrypoint.sh`）。2 系は `CURLOPT_SSL_VERIFYPEER => true` なので検証を切らずに通す |
| ホスト名 | `up` の時点で採番。Caddy のサイトアドレスと docker network alias の両方に同じ名前が要るため、テスト実行時には決められない |
| discovery の宛先 | `api.ec-auth.io`（両プラグインの既定値）を network alias で Caddy に引き込む。`ECAUTH_CLIENT_RESOLVE_URL` では上書きしない — 顧客が通る既定のままの経路を検証したいため |
| `PlatformApi:BaseDomain` | この E2E だけ `ec-auth.io` に上書きする（`compose.e2e-eccube.yaml`）。Development の既定 `localhost:8081` だと `{tenant}.localhost` が 2 セグメントになり、`TenantMiddleware` がテナントを取り出せない |
| EC-CUBE 本体のバージョン | プラグインリポジトリの `Dockerfile` の `FROM` が決める。compose 側にノブは置かない |

#### 再実行時の注意

申込は組織コードの重複を弾く（`organization_already_exists`）。組織コードはサイトホストから
導出されるため、**同じホストで二度申し込めない**。`up` は毎回新しい RUN_ID を振り、`down` は
ボリュームごと破棄する。`E2E_RUN_ID` を環境変数で固定すると同じホストで再実行してしまうので、
意図的に再現したいとき以外は設定しない。

### マイグレーション設計ルール

- `migrationBuilder.Sql()` でカラムを参照する UPDATE/INSERT 文を書く場合、`EXEC()` 動的 SQL でラップすること
  - CI/CD の `dotnet ef migrations script --idempotent` で生成されるスクリプトは全マイグレーションが 1 バッチにまとめられる
  - SQL Server はバッチ全体をコンパイル時に検証するため、同一マイグレーション内で追加したカラムを参照する DML はコンパイルエラーになる
  - `EXEC()` でラップすることで名前解決を実行時まで遅延させる
- 破壊的変更（カラム削除・リネーム）を伴うマイグレーションのデータ移行 SQL には `IF EXISTS` でカラム存在チェックを追加すること
  - これにより、関連するカラムを削除する後続のマイグレーションが既に適用されている環境でも、冪等スクリプトがエラーなく実行できるようになります。
- カラム存在チェックには `INFORMATION_SCHEMA.COLUMNS` ではなく `sys.columns` + `OBJECT_ID(N'dbo.table_name')` を使用し、DML でもスキーマ修飾（`dbo.`）を明示すること
