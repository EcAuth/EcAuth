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
| `/userinfo` | `auth_header_parse` / `token_transport_check` / `access_token_validate` / `user_lookup` |
| `/api/external-userinfo` | `auth_header_parse` / `access_token_validate` / `external_userinfo_fetch` |
| `register/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `fido2_make_credential` / `credential_persist` / `challenge_consume`） |
| `authenticate/verify` | `client_authenticate` / `service_call`（内訳: `challenge_lookup` / `session_client_verify` / `credential_lookup` / `credential_organization_verify` / `fido2_make_assertion` / `signcount_persist` / `challenge_consume`） |
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

### B2B パスキー authenticate/verify の検証レイヤ（WebAuthn §7.2）

`B2BPasskeyService.VerifyAuthenticationAsync` は assertion 検証の前に 5 段の検証を行う。
**どれも冗長ではない**（EcAuth#516 でこの多くが欠けていたことが判明した）。順序と目的:

| # | 検証 | 失敗理由ログ | 何を守るか |
|---|------|--------------|-----------|
| 1 | セッションとリクエスト元 Client の突合（`client.Id == challenge.ClientId`） | `session_client_mismatch` | 別 Client が発行したセッションを自分の `client_id` で持ち込み、認可コードを自分宛に発行させる経路 |
| 2 | §7.2 Step 5: `challenge.AllowedCredentialIds` との照合 | `credential_not_allowed` | このセッションで発行していないクレデンシャルでの認証 |
| 3 | §7.2 Step 6: `challenge.Subject` との照合 | `credential_subject_mismatch` | b2b_subject 指定経路で、確定済みユーザー以外のクレデンシャルでの認証。登録側の `ExpectedSubject` 突合と対称 |
| 4 | Organization スコープ（`credential.B2BSubject` が Client の Organization の `B2BUser` に属するか） | `credential_organization_mismatch` | **同一 rp_id を共有する別 Organization 間の越境**。同一ドメインで本番サイトとサンドボックスサイトの両方を申し込むと `allowed_rp_ids` が同値になりうるため、rpIdHash 検証では防げない |
| 5 | `isUserHandleOwner` コールバック（Fido2.NetLib 経由） | — | Step 6 の後者（ユーザー未確定経路）の要件 |

判断の根拠:

- **Organization 検証（#4）は #2 / #3 では代替できない。** `CreateAuthenticationOptionsAsync` の
  b2b_subject 指定経路は、リクエスト由来の `b2b_subject` をそのまま使う（この API は無認証で呼べる）。
  Organization で絞らなければ他 Organization のユーザーの b2b_subject を指定できてしまい、
  その場合 #2 / #3 は「発行した一覧」「確定した subject」と整合するので通過する。
  → **options 側でも Organization で絞る**ことと、verify 側の #4 の両方が必要。
  この不変条件は `AuthorizeByRegistrationTokenAsync` が登録トークンに対して既に課しているものと同じ。
- **`rpIdHash` 検証では足りない。** Fido2.NetLib の origin / rpIdHash 検証は
  `ServerDomain = challenge.RpId` に基づくため、rp_id が違うクレデンシャルは落ちる。
  逆に**同一 rp_id なら Organization / Client をまたいでも通る**。
- **#2 を自前で実装しているのは Fido2.NetLib と二重防御にするため。** `OriginalOptions.AllowCredentials`
  も発行時の値へ復元してライブラリ側の照合にも掛けているが、失敗理由を構造化ログに出し、
  ユニットテスト（`IFido2` はモック）で守れる形にするために自前チェックを残す。
- **`webauthn_challenge.allowed_credential_ids` の 3 状態**（NULL / `"[]"` / 要素あり）は意図的。
  NULL は「発行時に記録していない」（登録チャレンジ、カラム追加前の既存行）、`"[]"` は
  「allowCredentials を空で発行した」（discoverable credential フロー）。どちらも §7.2 Step 5 の
  「空でない場合」を満たさないため照合しないが、事後に理由を切り分けられるよう区別して保存する。
- **Client 境界（同一 Organization 内の Client 間）は #2 が担う。** ただし b2b_subject 未指定経路の
  allowCredentials は現在 Organization 単位で発行しているため、この経路では実質 Organization
  境界と同じになる。`CreateAuthenticationOptionsAsync` のコメントにある移行
  （rk フラグ保存 → `ResidentKey.Required` 化 → 既存ユーザー再登録 → 空 allowCredentials へ切替）を
  終えた時点で、#2 が自動的に Client 境界の強制になる。

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

#### 申込が作る Organization と組織コードの導出

申込は入力された URL ごとに独立した Organization を作る（最大 2 件）。組織コードは
そのままテナント名になり、プラグインが接続する `https://{tenant}.ec-auth.io` に現れる。

| サイト | 組織コード | `IsSandbox` |
|---|---|---|
| 本番 | ホスト名から導出（`shop.example.jp` → `shop-example-jp`） | `false` |
| テスト | 導出結果 + **`-sandbox`**（`stg.example.jp` → `stg-example-jp-sandbox`） | `true` |

導出は lowercase → 先頭 `www.` 除去 → 英数以外の連続を `-` に畳む（`SignupService.DeriveOrganizationCode`）。

**サンドボックスに必ず `-sandbox` が付く理由**は、本番と同じドメイン（あるいは `www.` の
有無だけが違うドメイン）でもテスト Org を作れるようにするため。付けないと導出後コードが
本番と衝突し、テスト環境を別ドメインで持たない顧客は検証にも本番 Org を使うしかなくなる。
副次的に、接続先 URL を見ただけで本番かサンドボックスかが判別できる。

同一ドメインで両方作った場合、2 つの Client は `allowed_rp_ids` も `redirect_uri` も同じ値に
なりうるが問題は起きない。プラグインは自分の `client_id` から `/platform/v1/client-resolve` で
テナントを引くため、資格情報を差し替えるだけで本番 / サンドボックスを行き来できる。

組織コードは DNS ラベル 1 つ分なので 63 文字を超えられない（`MaxOrganizationCodeLength`）。
超える申込は `invalid_site_url` で弾く。

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

### 確認メールの受信口（mailpit と E2E メールボックスの使い分け）

申込フローの E2E は確認メールの本文からトークンを取り出す必要がある（トークンは本文にしか
存在せず、DB には SHA-256 ハッシュしか残らない）。受信口は環境で変わる。

spec は受信口を直接触らず、`tests/helpers/mailbox.ts` の `Mailbox` 越しに読む。
実装の選択は `E2E_MAILBOX_KIND`（既定 `mailpit`）。

| 環境 | `E2E_MAILBOX_KIND` | 実装 |
|---|---|---|
| ローカル / CI | `mailpit`（既定） | `tests/helpers/mailpit.ts`（`MAILPIT_BASE_URL`、既定 `http://localhost:8025`） |
| production | `e2e-mailbox` | `tests/helpers/e2e-mailbox.ts`（`E2E_MAILBOX_BASE_URL` / `E2E_MAILBOX_API_TOKEN`） |

後始末が「ID の配列」ではなく `cleanup(宛先)` なのは、Worker 側が宛先単位でしか削除
できないため（1 メッセージ 1 行で保存し ID を返さない）。mailpit 実装は全 spec で
共有される単一インスタンスを壊さないよう、**自分が読んだ ID だけ**を覚えて消す。

デプロイ済み環境は SendGrid 送信なので mailpit が使えない。代わりに
`ecauth-infrastructure` が用意した受信口を使う（構成の詳細と設計上の制約は
[ecauth-infrastructure の CLAUDE.md](https://github.com/EcAuth/ecauth-infrastructure/blob/main/CLAUDE.md)
「E2E 用メールボックス」を参照）。

| 項目 | 値 |
|------|-----|
| ベース URL | `https://e2e-mail.ec-auth.io` |
| 宛先 | `e2e-{RUN_ID}@e2e.ec-auth.io` |
| 読み出しトークン | `op://EcAuth/ecauth-e2e-mailbox/api_token`（`Authorization: Bearer`） |
| API | `GET` / `DELETE /messages?to=<address>` |
| レスポンス | `{"messages":[{to, from, subject, text, html, received_at}, ...]}` |

呼び出し側の注意:

- 保存先は **D1**（以前は Workers KV）。KV は結果整合で、書き込みが読み出しに反映される
  まで数十秒かかるため既定タイムアウトを 180 秒にしていた。D1 は強整合なので待つ対象は
  SendGrid の配送だけになり、既定は **60 秒 / ポーリング 2 秒**（mailpit は 20 秒）。
  移行（EcAuth/ecauth-infrastructure#167）直後の production verify の実測で、
  申込 → 確認メール → Account トークン取得が **38.5 秒 → 12.3 秒**になった。
- メッセージは 1 時間で失効するため、後始末の `DELETE` は必須ではない。
- 本文のフィールド名が mailpit（`Text` / `HTML` / `Subject` / `ID`）と異なるが、
  `Mailbox` が `{subject, text, html}` に正規化するので spec 側は意識しなくてよい。

### 本番デプロイ後の申込スモーク

`production.yml` の `verify` ジョブは、シード済み Client を使う
`b2b_passkey_authentication.spec.ts` に加えて `signup_client_b2b_login.spec.ts` を
**実本番に対して**回す。CI（`main.yml`）が守るのはコードの整合性だけで、
「デプロイ済み環境の設定が実際の認証フローと噛み合っているか」は別物のため。

| 項目 | 決め |
|---|---|
| 申込先テナント | `stg-accounts`。本物の顧客が入る `accounts` は汚さない |
| `client_secret` | **必要**。`STG_ACCOUNTS_CLIENT_PUBLIC` が設定されていないので stg-accounts の管理コンソール Client は confidential。無いと「client_secretが正しくありません。」で落ちる |
| テナント解決 | Host ヘッダの差し替えではなく実ホスト名（`E2E_TENANT_BASE_DOMAIN=ec-auth.io`）。Cloudflare 配下では SNI と Host の不一致を避ける必要があり、オリジンへの直アクセスも許可 IP で塞がれている（`environments/production/main.tf` の `cloudflare_ip_restrictions`） |
| 疑似サイトのオリジン | `context.route` で stub。`.test` は公開解決されないため実体を持たせない |
| EC-CUBE 実物版 | 入れない。イメージビルドで 10 分前後かかり、本番 DB に残る Organization も run あたり 2 倍になる。プラグインの実コードは CI 側で担保する |

**staging には入れられない。** `AccountsOrganizationSeeder` は `ACCOUNTS_*` /
`STG_ACCOUNTS_*` が未設定の環境では Organization を投入せず（Account 機能は本番のみ）、
`environments/staging/main.tf` の `app_settings` にこれらは存在しない。
つまり staging には申込 API 自体が無い。

#### 残留データ

このスモークは本番 DB に Organization / Client / B2BUser / パスキーを **1 run あたり 1 組**
残す。クリーンアップコマンドは [EcAuth#487](https://github.com/EcAuth/EcAuth/issues/487) で未着手。
それまでは run ごとの識別子（`e2e-{timestamp}-{rand}-test`）を手掛かりに手で消す。
識別子は Playwright 出力の先頭 `[signup-smoke]` 行と、`verify` ジョブの Step Summary に出る。

#### インフラ変更後の再検証

`ecauth-infrastructure` の apply はアプリを再デプロイしないため、`app_settings` や DNS を
変えても EcAuth 側では誰も検証しない。この空白を埋めるため、`terraform.yml` の
`verify-staging` / `verify-production` ジョブが apply 成功後に
`gh workflow run <staging|production>.yml -f action=verify-only -f dry_run=false` を撃つ。
`verify-only` は migrate / build / deploy を skip して `verify` だけを回す入口。
`production.yml` の `dry_run` は既定 `true` なので、明示的に `false` を渡さないと verify も skip される。

### マイグレーション設計ルール

- `migrationBuilder.Sql()` でカラムを参照する UPDATE/INSERT 文を書く場合、`EXEC()` 動的 SQL でラップすること
  - CI/CD の `dotnet ef migrations script --idempotent` で生成されるスクリプトは全マイグレーションが 1 バッチにまとめられる
  - SQL Server はバッチ全体をコンパイル時に検証するため、同一マイグレーション内で追加したカラムを参照する DML はコンパイルエラーになる
  - `EXEC()` でラップすることで名前解決を実行時まで遅延させる
- 破壊的変更（カラム削除・リネーム）を伴うマイグレーションのデータ移行 SQL には `IF EXISTS` でカラム存在チェックを追加すること
  - これにより、関連するカラムを削除する後続のマイグレーションが既に適用されている環境でも、冪等スクリプトがエラーなく実行できるようになります。
- カラム存在チェックには `INFORMATION_SCHEMA.COLUMNS` ではなく `sys.columns` + `OBJECT_ID(N'dbo.table_name')` を使用し、DML でもスキーマ修飾（`dbo.`）を明示すること
- 制約の一意性は実 SQL Server でしか検証できない。**InMemory プロバイダーは一意インデックスを強制しない**ため、ユニット
  テストが通っても実 DB で落ちることがある（EcAuth#521 で実際に見逃していた）

### マイグレーションのデプロイ順序

`production.yml` / `staging.yml` のジョブ順は `migrate` → `deploy` → `verify`。この順序は**追加のみの
マイグレーションでは正しく、値の変換や削除では危険**。マイグレーションを書いた時点で種別を判定し、
PR 説明に明記すること。

なお `dry_run` の既定値は環境で違う。`staging.yml` は `false`（main への push でそのまま
migrate → deploy → verify が走る）だが、**`production.yml` は `true`** なので、本番で実際に流すには
`dry_run=false` を明示する必要がある。

| 種別 | 安全な順序 | 理由 |
|------|-----------|------|
| **追加のみ**（テーブル / カラム / 索引の追加、制約の緩和、新テーブルへの backfill） | `migrate-and-deploy`（既定のまま） | 旧コードは新構造を知らないので無害。新コードは新構造を必要とするので先に作る必要がある |
| **値の変換**（ハッシュ化・形式変更・既存カラムの書き換え） | `deploy-only` → ロールアウト確認 → `migrate-only` | 旧コードが変換後 DB に旧形式を書き込む窓を塞ぐ |
| **削除・制約強化**（カラム / テーブル削除、リネーム、UNIQUE 追加） | `deploy-only` → ロールアウト確認 → `migrate-only` | 旧コードが存在しない列を参照して落ちる。制約強化は旧コードが違反データを書き得る |

**追加と削除を同じリリースに混在させないこと。** 混在すると、どちらの順序を選んでも安全にならない。

- `deploy-only` 先行 → 新コードが必要とする追加分がまだ存在せず `Invalid object name` で落ちる
- `migrate-and-deploy` → 削除分が旧インスタンスを壊す

`migrate-only` は未適用マイグレーションを**全部**適用するため、片方だけ先に流すこともできない。
したがってリリースを分割する。

| リリース | 内容 | 順序 |
|---------|------|------|
| 1（expand） | 追加のみのマイグレーション + 旧構造を参照しなくなったコード | `migrate-and-deploy` |
| 2（contract） | 削除・制約強化のみのマイグレーション | `deploy-only`（コード変更があれば）→ ロールアウト確認 → `migrate-only` |

リリース 2 の時点で「旧構造を参照しないコード」は既に本番で動いているため、削除しても壊れない。
EcAuthDocs#110 がこの形（#521 が expand、`b2b_user.external_id` の削除が contract）。

**未適用マイグレーションを溜めないこと。** 複数溜まると種別が混ざり、この分割ができなくなる。

> ⚠️ **「常に deploy 先行」にしてはいけない。** 追加のみを deploy 先行にすると逆向きに壊れる。
> EcAuth#521（`b2b_user_identity` の新設）を `deploy-only` 先行でやっていたら、新コードが
> `Invalid object name 'b2b_user_identity'` で落ち、B2B パスキー登録と申込が全滅していた。

**UNIQUE を追加するときは順序だけでは足りない。** 既存データに重複があると `CREATE UNIQUE INDEX`
自体が失敗し、`migrate-only` がそこで止まる。デプロイ順序とは独立に、事前に実 SQL Server で重複を
検査して解消しておくこと。

```sql
SELECT col_a, col_b, COUNT(*) AS dup
FROM dbo.some_table
GROUP BY col_a, col_b
HAVING COUNT(*) > 1;
```

EcAuth#521 では、`b2b_user` の `(organization_id, external_id)` を UNIQUE へ戻す `Down` が
まさにこれで失敗することを実測している（`duplicate key value is (2, DUP-HASH)`）。この Down は
非一意で再作成する形に変更した。

効いているのは順序そのものではなく、**データが変わる瞬間に両形式を扱えるコードだけが動いている**
という状態。したがって `migrate-only` の前の**ロールアウト完了確認を省略しない**こと。`deploy` ジョブの
health check は `/healthz` が 200 を返すまで待つが、これは新コードが応答することを示すだけで、
旧インスタンスが残っていないことの証明にはならない。

**互換コードは読み取り側に絞る。** 旧構造への書き込みまで二重化すると「どちらが正か」の判定と衝突処理が
必要になる。旧構造は凍結して読むだけにすれば、移行期間中に増えるのはフォールバック 1 本で済む
（EcAuth#521 のレビュー指摘 4 件のうち 2 件は二重書きに起因していた）。

**シーダーが新しい表へ書くときは `RequiredMigration` をその表を作るマイグレーションへ更新する。**
`DbInitializer` は `RequiredMigration` が適用済みならシーダーを走らせるため、旧マイグレーション名の
ままだと表が無い環境で INSERT に到達し、起動初期化が落ちる。

詳細と実例は [マイグレーションのデプロイ順序 runbook](https://github.com/EcAuth/EcAuthDocs/blob/main/html/migration-deploy-runbook.html)（EcAuthDocs）を参照。
