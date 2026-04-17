#!/bin/bash
set -e

echo "🔧 Docker環境用のE2Eテストを実行します..."

# .env.docker が存在する場合は使用、なければ .env を使用
if [ -f .env.docker ]; then
    echo "📁 .env.docker を使用します"
    ENV_FILE=".env.docker"
else
    echo "📁 .env を使用します"
    ENV_FILE=".env"
fi

# Docker Compose を停止して再起動
echo "🛑 既存のコンテナを停止します..."
docker compose --env-file="$ENV_FILE" -p ec-auth down

echo "🚀 コンテナを起動します..."
docker compose --env-file="$ENV_FILE" -p ec-auth up -d --build

echo "⏳ サービスの起動を待っています..."

# dbサービスが健全な状態になるまで待つ
echo "   データベースの起動を待っています..."
for i in {1..30}; do
    if docker compose --env-file="$ENV_FILE" -p ec-auth exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q "SELECT 1" > /dev/null 2>&1; then
        echo "   ✅ データベースが起動しました"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "   ❌ データベースの起動がタイムアウトしました"
        exit 1
    fi
    sleep 2
done

# マイグレーションは identityprovider 起動時に RUN_MIGRATIONS_ON_STARTUP=true により自動適用される
# （MockOpenIdProvider は Azure 環境で運用しているためローカルマイグレーション不要）

# HTTPSポートが応答するまで待つ
echo "⏳ アプリケーションの起動を待っています..."

# IdentityProvider (8081) の起動を待つ
echo "   IdentityProvider (https://localhost:8081) を待っています..."
for i in {1..30}; do
    if curl -k -s -o /dev/null -w '%{http_code}' https://localhost:8081/.well-known/openid-configuration | grep -q '200\|404'; then
        echo "   ✅ IdentityProvider が起動しました"
        break
    fi
    if [ $i -eq 30 ]; then
        echo "   ❌ IdentityProvider の起動がタイムアウトしました"
        docker compose --env-file="$ENV_FILE" -p ec-auth logs identityprovider
        exit 1
    fi
    sleep 2
done

echo "✅ すべてのサービスが起動しました"

# E2Eテストを実行
echo "🧪 E2Eテストを実行します..."

# E2Eテスト用の環境変数をエクスポート
export $(cat "$ENV_FILE" | grep -E '^E2E_' | xargs)

cd E2ETests
yarn install
npx playwright test --reporter=list

# テスト終了コードを保持
TEST_EXIT_CODE=$?

cd ..

# テスト結果に基づいてメッセージを表示
if [ $TEST_EXIT_CODE -eq 0 ]; then
    echo "✅ E2Eテストが成功しました！"
else
    echo "❌ E2Eテストが失敗しました"
    echo "📋 ログを確認してください:"
    echo ""
    echo "IdentityProvider のログ:"
    docker compose --env-file="$ENV_FILE" -p ec-auth logs --tail=50 identityprovider
fi

exit $TEST_EXIT_CODE