# E2ETests.Selenium

実ブラウザ（Chrome + ChromeDriver）で動かす **軽量な smoke テスト** を集めた C#（xUnit /
Selenium.WebDriver）プロジェクトです。フル機能の E2E（WebAuthn 署名検証やフェデレーション
認可コードフロー）は引き続き Playwright 版 [`../E2ETests`](../E2ETests) が担当し、ここはその
補完として「ページ/エンドポイントがブラウザから正しく配信される」ことだけを素早く確認します。

## テスト一覧

| テスト | 確認内容 |
|--------|----------|
| `OidcDiscoverySmokeTests` | `/.well-known/openid-configuration` と `/.well-known/jwks.json` をブラウザで開き、主要キー（`issuer` / `token_endpoint` / `jwks_uri`、RS256 公開鍵）が本文に現れること |
| `B2bPasskeyPageSmokeTests` | `/b2b-passkey-test.html` が配信され、タイトル・見出し・登録フォームの主要要素が描画されること |

## 前提

テストは起動済みの IdentityProvider（既定 `https://localhost:8081`）に接続します。先に
ローカル Docker 環境を起動してください。

```bash
docker compose -p ec-auth up -d --build
```

## ChromeDriver の解決方針

`ChromeDriverFixture` は **PATH（または `CHROMEWEBDRIVER`）上の `chromedriver` を明示的に解決**
して使用します。これは CI で [`nanasess/setup-chromedriver`](https://github.com/nanasess/setup-chromedriver)
が導入したドライバをそのまま使うことを意図したものです。ローカルで `chromedriver` が見つからない
場合のみ、Selenium Manager による自動ダウンロードにフォールバックします。

## ローカル実行

```bash
# IdP 起動後に実行
dotnet test E2ETests.Selenium/E2ETests.Selenium.csproj

# ベース URL を上書きする場合
E2E_BASE_URL=https://localhost:8081 dotnet test E2ETests.Selenium/E2ETests.Selenium.csproj
```

テストはヘッドレス Chrome（`--headless=new`）で動作し、自己署名証明書を許可
（`AcceptInsecureCertificates`）します。

## CI

[`.github/workflows/selenium.yml`](../.github/workflows/selenium.yml) が、SQL Server 起動 →
.NET ビルド → DB マイグレーション → IdP 起動 → `nanasess/setup-chromedriver` で ChromeDriver
導入 → `dotnet test` の順で実行します。Playwright ワークフロー（`playwright.yml`）とは独立した
ジョブです。`nanasess/setup-chromedriver` は full commit SHA でピン留めしつつ、末尾コメント
`# main` で action の `main` ブランチに追従させます（最新ダイジェストへの更新は `renovate.json`
の `pinDigests` で Renovate が自動化）。これにより、リリース前の action 最新版を EcAuth の
Selenium CI で継続的にドッグフーディングしながら、実行時は SHA 固定で再現性を担保できます。
