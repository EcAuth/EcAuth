#!/bin/bash
#
# 対象 App Service の app_settings から client_secret 暗号化用の Key Vault 鍵 URI を取得する。
# Terraform が管理する app_settings（単一ソース）を再利用し、本番 Key Vault の URL を
# リポジトリに焼き込まないための間接取得。
#
# 必須環境変数:
#   APP_NAME  対象 Web App 名（例: ecauth-staging-xxxx）
# 出力:
#   $GITHUB_OUTPUT に key_id=<Key Vault 鍵 URI>
#
# 前提: 事前に azure/login 済みで az CLI がサブスクリプションにアクセスできること。

set -euo pipefail

if [ -z "${APP_NAME:-}" ]; then
  echo "APP_NAME が未設定です" >&2
  exit 1
fi

APP_ID=$(az webapp list --query "[?name=='${APP_NAME}'].id | [0]" -o tsv)
if [ -z "${APP_ID}" ]; then
  echo "Web App が見つかりません: ${APP_NAME}" >&2
  exit 1
fi

KEY_ID=$(az webapp config appsettings list --ids "${APP_ID}" \
  --query "[?name=='ClientSecretProtection__KeyVaultKeyId'].value | [0]" -o tsv)
if [ -z "${KEY_ID}" ]; then
  echo "ClientSecretProtection__KeyVaultKeyId が ${APP_NAME} の app_settings に設定されていません" >&2
  echo "（インフラ側の鍵配線が完了しているか確認してください）" >&2
  exit 1
fi

# 非秘密だが、本番 Key Vault の URL をログに残さないためマスクする。
echo "::add-mask::${KEY_ID}"
echo "key_id=${KEY_ID}" >> "${GITHUB_OUTPUT}"
echo "Resolved Key Vault key id for ${APP_NAME} (masked)."
