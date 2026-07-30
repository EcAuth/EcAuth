#!/usr/bin/env bash
#
# EC-CUBE 4 系 / 2 系のプラグインを実際に動かす結合 E2E のランチャー。
#
#   ./E2ETests/scripts/eccube-e2e.sh up            # ホスト名を採番して compose を起動
#   ./E2ETests/scripts/eccube-e2e.sh test [args]   # Playwright を実行（args はそのまま渡す）
#   ./E2ETests/scripts/eccube-e2e.sh logs [svc]
#   ./E2ETests/scripts/eccube-e2e.sh down          # 停止（ボリュームも削除）
#
# ホスト名を up の時点で確定させるのは、Caddy のサイトアドレスと docker network
# alias の両方へ同じ名前を配る必要があるため（compose.e2e-eccube.yaml 冒頭参照）。
# 採番結果は .e2e-eccube.env に残し、test / logs / down から読み直す。
#
# 申込は組織コードの重複を弾く（SignupService の organization_already_exists）。
# up のたびに新しい RUN_ID を振り、down で EcAuth の DB ごと落とすことで、
# 同じホスト名で二度申し込む状況を作らない。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STATE_FILE="${ROOT}/.e2e-eccube.env"
ENV_TEMPLATE="${E2E_ENV_TEMPLATE:-${ROOT}/.env.dev.tpl}"

compose() {
    docker compose -p ec-auth \
        -f "${ROOT}/compose.yaml" \
        -f "${ROOT}/compose.e2e-eccube.yaml" \
        "$@"
}

# 採番した設定を環境へ載せる。無ければ理由を添えて止める（暗黙の既定値を作らない）。
load_state() {
    if [ ! -f "${STATE_FILE}" ]; then
        echo "${STATE_FILE} がありません。先に '$0 up' を実行してください。" >&2
        exit 1
    fi
    set -a
    # shellcheck disable=SC1090
    . "${STATE_FILE}"
    set +a
}

cmd_up() {
    # RUN_ID はホスト名の 1 ラベルに収まる英数字。組織コードは
    # e2e-{RUN_ID}-shop4-test のようにホストから導出される（DeriveOrganizationCode）。
    local run_id="${E2E_RUN_ID:-$(date +%s)-$RANDOM}"

    # 秘密は入らない（ホスト名とディレクトリのみ）。再実行のたびに作り直すのが正しいので
    # 追記ではなく生成する。プロジェクトの .env とは別ファイル。
    cat > "${STATE_FILE}" <<EOF
# eccube-e2e.sh up が生成。手で編集しない（次の up で上書きされる）。
E2E_RUN_ID=${run_id}
E2E_ECCUBE4_SHOP_HOST=e2e-${run_id}.shop4.test
E2E_ECCUBE2_SHOP_HOST=e2e-${run_id}.shop2.test
E2E_ECCUBE4_TENANT_HOST=e2e-${run_id}-shop4-test.ec-auth.io
E2E_ECCUBE2_TENANT_HOST=e2e-${run_id}-shop2-test.ec-auth.io
E2E_ECCUBE4_PLUGIN_DIR=${E2E_ECCUBE4_PLUGIN_DIR:-../ec-cube4-ecauth}
E2E_ECCUBE2_PLUGIN_DIR=${E2E_ECCUBE2_PLUGIN_DIR:-../ec-cube2-ecauth}
EOF
    load_state

    echo "[eccube-e2e] RUN_ID=${E2E_RUN_ID}"
    echo "[eccube-e2e] 4系 shop=${E2E_ECCUBE4_SHOP_HOST} tenant=${E2E_ECCUBE4_TENANT_HOST}"
    echo "[eccube-e2e] 2系 shop=${E2E_ECCUBE2_SHOP_HOST} tenant=${E2E_ECCUBE2_TENANT_HOST}"

    # identityprovider 側のシークレットは op run で注入する（平文 .env は作らない）。
    # CI など op を使わない環境では E2E_SKIP_OP=1 で素の docker compose に落とす。
    if [ "${E2E_SKIP_OP:-}" = "1" ]; then
        compose up -d --build --wait
    else
        op run --env-file="${ENV_TEMPLATE}" -- \
            docker compose -p ec-auth \
                -f "${ROOT}/compose.yaml" \
                -f "${ROOT}/compose.e2e-eccube.yaml" \
                up -d --build --wait
    fi

    # --wait は healthcheck を持たないサービスを「起動した」としか見ない。EC-CUBE は
    # entrypoint が本体インストールとプラグイン導入を終えてから Apache を exec するので、
    # 「HTTP で応答する」= 準備完了。Caddy 越しに叩いて経路ごと確認する。
    wait_for_url "${E2E_ECCUBE4_TENANT_HOST}" /healthz 'EcAuth（4系テナント）' caddy
    wait_for_url "${E2E_ECCUBE2_TENANT_HOST}" /healthz 'EcAuth（2系テナント）' caddy
    wait_for_url "${E2E_ECCUBE4_SHOP_HOST}" /admin/login 'EC-CUBE 4系' ec-cube4
    wait_for_url "${E2E_ECCUBE2_SHOP_HOST}" "${E2E_ECCUBE2_ADMIN_BASE:-/admin/}" 'EC-CUBE 2系' ec-cube2

    echo "[eccube-e2e] 準備完了。'$0 test' で Playwright を実行してください。"
}

# Caddy が 443 で受けるホストを、ホスト側の DNS に手を入れずに叩く。
# --resolve で名前解決だけを差し替えるので SNI / Host ヘッダは本番同様のまま。
# 証明書は Caddy のローカル CA 発行なので -k で通す（TLS 検証は EC-CUBE 側で行う）。
wait_for_url() {
    local host="$1" path="$2" label="$3" service="$4"
    local attempts="${E2E_WAIT_ATTEMPTS:-120}"

    echo "[eccube-e2e] Waiting for ${label}: https://${host}${path}"
    for _ in $(seq 1 "${attempts}"); do
        if curl -sfk --resolve "${host}:443:127.0.0.1" --max-time 10 \
                -o /dev/null "https://${host}${path}"; then
            echo "[eccube-e2e] ${label} ready"
            return 0
        fi
        sleep 5
    done

    echo "[eccube-e2e] ${label} の起動待ちが上限に達しました。直近のログ:" >&2
    compose logs --no-color --tail 120 "${service}" >&2 || true
    return 1
}

cmd_test() {
    load_state
    cd "${ROOT}/E2ETests"
    pnpm exec playwright test "$@"
}

cmd_logs() {
    load_state
    compose logs --no-color "$@"
}

cmd_down() {
    if [ -f "${STATE_FILE}" ]; then
        load_state
    else
        # down だけ先に呼ばれても compose の必須変数チェックで落ちないようにする。
        export E2E_ECCUBE4_SHOP_HOST=unset.shop4.test
        export E2E_ECCUBE2_SHOP_HOST=unset.shop2.test
        export E2E_ECCUBE4_TENANT_HOST=unset-shop4-test.ec-auth.io
        export E2E_ECCUBE2_TENANT_HOST=unset-shop2-test.ec-auth.io
    fi
    compose down -v
    rm -f "${STATE_FILE}"
}

case "${1:-}" in
    up) shift; cmd_up "$@" ;;
    test) shift; cmd_test "$@" ;;
    logs) shift; cmd_logs "$@" ;;
    down) shift; cmd_down "$@" ;;
    *)
        echo "usage: $0 {up|test|logs|down} [args...]" >&2
        exit 2
        ;;
esac
