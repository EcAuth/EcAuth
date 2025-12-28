#!/bin/bash
#
# EcAuth フェデレーション認証確認スクリプト
#
# 必須環境変数:
#   ECAUTH_BASE_URL  - EcAuth のベース URL
#   MOCK_USER        - MockIdP のテストユーザーメールアドレス
#   MOCK_PASS        - MockIdP のテストユーザーパスワード
#   CLIENT_ID        - EcAuth クライアント ID
#   CLIENT_SECRET    - EcAuth クライアントシークレット
#
# オプション環境変数:
#   GITHUB_OUTPUT    - GitHub Actions の output ファイル（設定されていれば出力を書き込む）
#   GITHUB_STEP_SUMMARY - GitHub Actions の step summary ファイル
#
set -eo pipefail

# 色付き出力（GitHub Actions では無効化）
if [ -n "$GITHUB_ACTIONS" ]; then
  RED=""
  GREEN=""
  YELLOW=""
  NC=""
else
  RED='\033[0;31m'
  GREEN='\033[0;32m'
  YELLOW='\033[1;33m'
  NC='\033[0m'
fi

# ログ関数
log_info() {
  echo -e "${GREEN}✅ $1${NC}"
}

log_error() {
  echo -e "${RED}❌ $1${NC}"
}

log_step() {
  echo -e "${YELLOW}=== $1 ===${NC}"
}

# GitHub Actions output に書き込む
write_output() {
  local key=$1
  local value=$2
  if [ -n "$GITHUB_OUTPUT" ]; then
    echo "${key}=${value}" >> "$GITHUB_OUTPUT"
  fi
}

# GitHub Actions step summary に書き込む
write_summary() {
  if [ -n "$GITHUB_STEP_SUMMARY" ]; then
    echo "$1" >> "$GITHUB_STEP_SUMMARY"
  fi
}

# 必須環境変数のチェック
check_required_vars() {
  local missing=0
  for var in ECAUTH_BASE_URL MOCK_USER MOCK_PASS CLIENT_ID CLIENT_SECRET; do
    if [ -z "${!var}" ]; then
      log_error "Required environment variable $var is not set"
      missing=1
    fi
  done
  if [ $missing -eq 1 ]; then
    exit 1
  fi
}

# メイン処理
main() {
  check_required_vars

  write_summary "## フェデレーション認証確認結果"
  write_summary ""
  write_summary "| ステップ | 結果 |"
  write_summary "|----------|------|"

  # Step 1: EcAuth 認可エンドポイント
  log_step "Step 1: EcAuth 認可エンドポイント"
  MOCKIDP_URL=$(curl -s -i "${ECAUTH_BASE_URL}/authorization?client_id=${CLIENT_ID}&redirect_uri=https%3A%2F%2Flocalhost%3A8081%2Fauth%2Fcallback&response_type=code&scope=openid%20profile%20email&provider_name=staging-mockidp&state=test123" 2>&1 | grep -i "^location:" | sed 's/location: //i' | tr -d '\r')

  if [ -z "$MOCKIDP_URL" ]; then
    log_error "Failed to get MockIdP redirect URL"
    write_summary "| 認可エンドポイント | ❌ Failed |"
    write_output "result" "failure"
    write_output "failed_step" "authorization_endpoint"
    exit 1
  fi
  log_info "Got MockIdP redirect URL"
  write_summary "| 認可エンドポイント | ✅ |"

  # Step 2: MockIdP で認証
  log_step "Step 2: MockIdP で認証"
  ECAUTH_CALLBACK=$(curl -s -i -u "${MOCK_USER}:${MOCK_PASS}" "$MOCKIDP_URL" 2>&1 | grep -i "^location:" | sed 's/location: //i' | tr -d '\r')

  if [ -z "$ECAUTH_CALLBACK" ]; then
    log_error "Failed to authenticate with MockIdP"
    write_summary "| MockIdP 認証 | ❌ Failed |"
    write_output "result" "failure"
    write_output "failed_step" "mockidp_authentication"
    exit 1
  fi
  log_info "Authenticated with MockIdP"
  write_summary "| MockIdP 認証 | ✅ |"

  # Step 3: EcAuth 認可画面で承認
  log_step "Step 3: EcAuth 認可画面で承認"
  MOCKIDP_CODE=$(echo "$ECAUTH_CALLBACK" | sed -n 's/.*code=\([^&]*\).*/\1/p')
  STATE=$(echo "$ECAUTH_CALLBACK" | sed -n 's/.*state=\([^&]*\).*/\1/p')

  FINAL_REDIRECT=$(curl -s -i -X POST "${ECAUTH_BASE_URL}/auth/callback" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "code=${MOCKIDP_CODE}" \
    -d "state=${STATE}" \
    -d "scope=" \
    -d "action=authorize" 2>&1 | grep -i "^location:" | sed 's/location: //i' | tr -d '\r')

  ECAUTH_CODE=$(echo "$FINAL_REDIRECT" | sed -n 's/.*code=\([^&]*\).*/\1/p')
  if [ -z "$ECAUTH_CODE" ]; then
    log_error "Failed to get authorization code from EcAuth"
    write_summary "| 認可画面承認 | ❌ Failed |"
    write_output "result" "failure"
    write_output "failed_step" "authorization_approval"
    exit 1
  fi
  log_info "Got EcAuth authorization code"
  write_summary "| 認可画面承認 | ✅ |"
  write_output "authorization_code" "$ECAUTH_CODE"

  # Step 4: トークン取得
  log_step "Step 4: トークン取得"
  TOKEN_RESPONSE=$(curl -s -X POST "${ECAUTH_BASE_URL}/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "grant_type=authorization_code" \
    -d "code=${ECAUTH_CODE}" \
    -d "redirect_uri=https://localhost:8081/auth/callback" \
    -d "client_id=${CLIENT_ID}" \
    -d "client_secret=${CLIENT_SECRET}")

  ACCESS_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.access_token')
  TOKEN_TYPE=$(echo "$TOKEN_RESPONSE" | jq -r '.token_type')
  EXPIRES_IN=$(echo "$TOKEN_RESPONSE" | jq -r '.expires_in')

  if [ -z "$ACCESS_TOKEN" ] || [ "$ACCESS_TOKEN" = "null" ]; then
    log_error "Failed to get access token"
    # セキュリティ: トークン情報を除外してエラー情報のみ出力
    echo "Error: $(echo "$TOKEN_RESPONSE" | jq -c '{error, error_description}' 2>/dev/null || echo 'Failed to parse response')"
    write_summary "| トークン取得 | ❌ Failed |"
    write_output "result" "failure"
    write_output "failed_step" "token_endpoint"
    exit 1
  fi
  log_info "Got access token (type: $TOKEN_TYPE, expires_in: $EXPIRES_IN)"
  write_summary "| トークン取得 | ✅ |"
  write_output "token_type" "$TOKEN_TYPE"
  write_output "expires_in" "$EXPIRES_IN"

  # Step 5: ユーザー情報取得
  log_step "Step 5: ユーザー情報取得"
  USERINFO_RESPONSE=$(curl -s "${ECAUTH_BASE_URL}/userinfo" \
    -H "Authorization: Bearer ${ACCESS_TOKEN}")

  SUB=$(echo "$USERINFO_RESPONSE" | jq -r '.sub')
  if [ -z "$SUB" ] || [ "$SUB" = "null" ]; then
    log_error "Failed to get user info"
    # セキュリティ: エラー情報のみ出力（PII を含む可能性があるため全体を出力しない）
    echo "Error: $(echo "$USERINFO_RESPONSE" | jq -c '{error, error_description}' 2>/dev/null || echo 'Failed to parse response')"
    write_summary "| ユーザー情報取得 | ❌ Failed |"
    write_output "result" "failure"
    write_output "failed_step" "userinfo_endpoint"
    exit 1
  fi
  log_info "Got user info: sub=$SUB"
  write_summary "| ユーザー情報取得 | ✅ |"
  write_output "user_sub" "$SUB"

  # 完了
  log_step "フェデレーション認証確認完了"
  write_summary ""
  write_summary "**User Subject:** \`$SUB\`"
  write_output "result" "success"

  echo ""
  echo "All federation authentication steps completed successfully!"
}

main "$@"
