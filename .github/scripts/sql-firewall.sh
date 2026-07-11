#!/bin/bash
#
# backfill 実行のため、GitHub ランナーの IP を Azure SQL Server のファイアウォールに
# 一時許可 / 解除する。azure/sql-action が内部で行うのと同じことを、.NET アプリ
# （ConsoleApp backfill）から直接接続するために明示的に行う。
#
# 使い方:
#   sql-firewall.sh add      ランナー IP を一時許可
#   sql-firewall.sh remove   一時許可を解除（常に実行する。失敗しても無視）
#
# 必須環境変数:
#   SQL_HOST  Azure SQL の FQDN（例: ecauth-staging-sql-xxxx.database.windows.net）
#   RUN_ID    実行ごとに一意なルール名を作るための ID（github.run_id）
# 前提: 事前に azure/login 済みで、identity が SQL Server のファイアウォール管理権限を持つこと。

set -euo pipefail

ACTION="${1:?add または remove を指定してください}"

if [ -z "${SQL_HOST:-}" ] || [ -z "${RUN_ID:-}" ]; then
  echo "SQL_HOST / RUN_ID が未設定です" >&2
  exit 1
fi

# FQDN からサーバーのリソース名を取り出す（.database.windows.net を除去）。
SQL_SERVER="${SQL_HOST%%.database.windows.net}"
RULE_NAME="gh-backfill-${RUN_ID}"

# リソースグループはサーバー名でサーバーサイド解決する。
# az の一過性失敗で set -e により即死しないよう || true を付ける。
RESOURCE_GROUP=$(az resource list --name "${SQL_SERVER}" --resource-type "Microsoft.Sql/servers" --query "[0].resourceGroup" -o tsv 2>/dev/null || true)
if [ -z "${RESOURCE_GROUP}" ]; then
  echo "SQL Server が見つかりません: ${SQL_SERVER}" >&2
  # remove は always() のクリーンアップで走るため、RG 未解決でもワークフローを
  # 失敗扱いにしない（best-effort）。add は許可できないと backfill も失敗するため fail-fast。
  if [ "${ACTION}" = "remove" ]; then
    exit 0
  fi
  exit 1
fi

case "${ACTION}" in
  add)
    RUNNER_IP=$(curl -fsS https://api.ipify.org)
    if [ -z "${RUNNER_IP}" ]; then
      echo "ランナー IP を取得できませんでした" >&2
      exit 1
    fi
    az sql server firewall-rule create \
      -g "${RESOURCE_GROUP}" -s "${SQL_SERVER}" -n "${RULE_NAME}" \
      --start-ip-address "${RUNNER_IP}" --end-ip-address "${RUNNER_IP}" --output none
    echo "一時ファイアウォールルールを追加しました: ${RULE_NAME}"
    ;;
  remove)
    # クリーンアップは冪等・ベストエフォート（存在しなくてもエラーにしない）。
    az sql server firewall-rule delete \
      -g "${RESOURCE_GROUP}" -s "${SQL_SERVER}" -n "${RULE_NAME}" --output none || true
    echo "一時ファイアウォールルールを削除しました: ${RULE_NAME}"
    ;;
  *)
    echo "不明なアクション: ${ACTION}（add または remove）" >&2
    exit 1
    ;;
esac
