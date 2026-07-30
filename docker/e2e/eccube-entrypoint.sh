#!/bin/bash
#
# EC-CUBE コンテナ（4 系 / 2 系共通）の entrypoint ラッパー。
# 本来の entrypoint を引数に取り、前処理を済ませてから exec する。
#
#   entrypoint: ["/bin/bash", "/e2e/eccube-entrypoint.sh", "/docker-entrypoint-plugin.sh"]
#   command:    ["apache2-foreground"]
#
# やることは 2 つだけ。どちらもプラグイン側のリポジトリを汚さずに済ませたいので、
# EcAuth 側の E2E オーバーレイからマウントして注入する。
set -eo pipefail

# --- 1. Caddy のローカル CA を信頼ストアへ ------------------------------------
#
# プラグインは https://{tenant}.ec-auth.io へサーバー間通信する。2 系は
# CURLOPT_SSL_VERIFYPEER => true（SC_Helper_EcAuthLogin2::httpRequest）、4 系は
# Symfony HttpClient で、いずれも既定で証明書を検証する。検証を切ると「本番では
# 検証が有効」という前提を E2E が踏まなくなるため、切らずに CA を信頼させる。
#
# Debian 系では update-ca-certificates が /etc/ssl/certs/ca-certificates.crt を
# 更新し、cURL / OpenSSL の既定バンドルがそれを指すため、PHP 側の設定変更は要らない。
CA_SRC=/caddy-data/caddy/pki/authorities/local/root.crt
CA_DST=/usr/local/share/ca-certificates/caddy-e2e-root.crt

# ルート証明書は Caddy が初回起動時に生成する。compose の depends_on では
# 「プロセスが上がった」ことしか保証されないので、ファイルの出現を待つ。
for i in $(seq 1 60); do
    if [ -s "${CA_SRC}" ]; then
        break
    fi
    if [ "${i}" -eq 1 ]; then
        echo "[e2e-entrypoint] Waiting for Caddy local CA at ${CA_SRC} ..."
    fi
    sleep 1
done

if [ -s "${CA_SRC}" ]; then
    cp "${CA_SRC}" "${CA_DST}"
    update-ca-certificates >/dev/null
    echo "[e2e-entrypoint] Installed Caddy local CA into the system trust store"
else
    # ここで落とすとプラグインのインストールログすら残らず原因が追いにくい。
    # 起動は続行し、TLS 検証エラーとして後段のテストで顕在化させる。
    echo "[e2e-entrypoint] WARNING: Caddy local CA not found; EcAuth への TLS 接続は失敗します" >&2
fi

# --- 2. X-Forwarded-Proto を PHP の HTTPS 環境変数へ ---------------------------
#
# Caddy が TLS を終端し、コンテナへは平文 HTTP で届く。素のままだと
#   - 4 系: generateUrl(..., ABSOLUTE_URL) が http:// のコールバック URL を作る
#           （PasskeyAuthController）→ 登録済み redirect_uri と完全一致せず 400
#   - 2 系: SC_Utils::sfIsHTTPS() が false になり HTTPS 前提の導線が崩れる
# となる。
#
# Symfony の TRUSTED_PROXIES を使う手もあるが、$_SERVER['HTTPS'] を立てる方が
# 2 系（Symfony 非依存）と共通化でき、EC-CUBE 側の設定に踏み込まずに済む。
# ポートは付けない: Host ヘッダにポートが無ければ Symfony の getPort() は
# スキームから 443 を導くため、getHttpHost() はホスト名だけを返す。
cat > /etc/apache2/conf-enabled/e2e-forwarded-proto.conf <<'CONF'
# E2E 専用。Caddy が付ける X-Forwarded-Proto を PHP から見える HTTPS に写す。
SetEnvIf X-Forwarded-Proto "^https$" HTTPS=on
CONF

exec "$@"
