# 1Password テンプレートファイル（ローカル Docker 開発環境用）
# 使用方法: op inject -i .env.dev.tpl -o .env
#
# EcAuth DB: Docker 内（固定値）
# MockIdP: Azure dev 環境（1Password から取得）

# =============================================================================
# Database Configuration (Docker 内)
# =============================================================================
DB_HOST=db
MIGRATION_DB_HOST=localhost
DB_NAME=EcAuthDb
DB_USER=SA
DB_PASSWORD='<YourStrong@Passw0rd>'
STATE_PASSWORD='<strong_password_string_of_at_least_32_characters>'

# =============================================================================
# Default Organization/Client Settings
# =============================================================================
DEFAULT_ORGANIZATION_CODE=example
DEFAULT_ORGANIZATION_NAME=example
DEFAULT_ORGANIZATION_TENANT_NAME=example
DEFAULT_ORGANIZATION_REDIRECT_URI=https://localhost:8081/auth/callback
DEFAULT_CLIENT_ID=client_id
DEFAULT_CLIENT_SECRET=client_secret
DEFAULT_APP_NAME=app_name

# =============================================================================
# Azure MockIdP (dev organization) Settings
# =============================================================================
MOCK_IDP_BASE_URL=op://EcAuth/mockidp-dev/base_url

# dev organization の MockIdP クライアント設定
MOCK_IDP_DEFAULT_CLIENT_ID=op://EcAuth/mockidp-dev/default_client_id
MOCK_IDP_DEFAULT_CLIENT_SECRET=op://EcAuth/mockidp-dev/default_client_secret
MOCK_IDP_DEFAULT_CLIENT_NAME=MockClient
MOCK_IDP_DEFAULT_USER_EMAIL=op://EcAuth/mockidp-dev/default_user_email
MOCK_IDP_DEFAULT_USER_PASSWORD=op://EcAuth/mockidp-dev/default_user_password

# Federate クライアント設定（EcAuth -> Azure MockIdP 連携用）
MOCK_IDP_FEDERATE_CLIENT_ID=op://EcAuth/mockidp-dev/federate_client_id
MOCK_IDP_FEDERATE_CLIENT_SECRET=op://EcAuth/mockidp-dev/federate_client_secret
MOCK_IDP_FEDERATE_CLIENT_NAME=FederateClient
MOCK_IDP_FEDERATE_USER_EMAIL=federate@example.jp

# =============================================================================
# Federate OAuth2 Settings (EcAuth -> Azure MockIdP)
# =============================================================================
FEDERATE_OAUTH2_APP_NAME=federate-oauth2
FEDERATE_OAUTH2_CLIENT_ID=op://EcAuth/mockidp-dev/federate_client_id
FEDERATE_OAUTH2_CLIENT_SECRET=op://EcAuth/mockidp-dev/federate_client_secret
FEDERATE_OAUTH2_AUTHORIZATION_ENDPOINT=op://EcAuth/mockidp-dev/authorization_endpoint
FEDERATE_OAUTH2_TOKEN_ENDPOINT=op://EcAuth/mockidp-dev/token_endpoint
FEDERATE_OAUTH2_USERINFO_ENDPOINT=op://EcAuth/mockidp-dev/userinfo_endpoint

# =============================================================================
# Google OAuth2 Settings (オプション)
# =============================================================================
GOOGLE_OAUTH2_APP_NAME=google-oauth2
GOOGLE_OAUTH2_CLIENT_ID=<Google OAuth2 client_id>
GOOGLE_OAUTH2_CLIENT_SECRET=<Google OAuth2 client_secret>
GOOGLE_OAUTH2_DISCOVERY_URL=https://accounts.google.com/.well-known/openid-configuration

# =============================================================================
# Amazon OAuth2 Settings (オプション)
# =============================================================================
AMAZON_OAUTH2_APP_NAME=amazon-oauth2
AMAZON_OAUTH2_CLIENT_ID=<Amazon OAuth2 client_id>
AMAZON_OAUTH2_CLIENT_SECRET=<Amazon OAuth2 client_secret>
AMAZON_OAUTH2_AUTHORIZATION_ENDPOINT=https://www.amazon.com/ap/oa
AMAZON_OAUTH2_TOKEN_ENDPOINT=https://api.amazon.co.jp/auth/o2/token
AMAZON_OAUTH2_USERINFO_ENDPOINT=https://api.amazon.com/user/profile

# =============================================================================
# Staging Organization/Client Settings (ローカル開発用ダミー値)
# =============================================================================
STAGING_ORGANIZATION_CODE=staging
STAGING_ORGANIZATION_NAME=staging
STAGING_ORGANIZATION_TENANT_NAME=staging
STAGING_CLIENT_ID=staging_client_id
STAGING_CLIENT_SECRET=staging_client_secret
STAGING_APP_NAME=EC-CUBE4 Staging
STAGING_REDIRECT_URI=https://localhost:4430/callback

# Staging MockIdP Settings
STAGING_MOCK_IDP_APP_NAME=staging-mock-idp
STAGING_MOCK_IDP_CLIENT_ID=staging_mock_idp_client_id
STAGING_MOCK_IDP_CLIENT_SECRET=staging_mock_idp_client_secret
STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT=https://localhost:9091/authorization?org=staging
STAGING_MOCK_IDP_TOKEN_ENDPOINT=https://localhost:9091/token
STAGING_MOCK_IDP_USERINFO_ENDPOINT=https://localhost:9091/userinfo

# =============================================================================
# EC-CUBE 2系プラグイン Settings (ローカル開発用ダミー値)
# =============================================================================
ECCUBE2_CLIENT_ID=eccube2_client_id
ECCUBE2_CLIENT_SECRET=eccube2_client_secret
ECCUBE2_REDIRECT_URI=https://localhost:4430/eccube2/callback

# =============================================================================
# B2B Passkey Test Data (Development)
# =============================================================================
DEV_B2B_USER_SUBJECT=3f7c0ab4-b004-4102-b6ed-a730369dd237
DEV_B2B_USER_EXTERNAL_ID=test-admin
DEV_B2B_REDIRECT_URI=https://localhost:8081/admin/ecauth/callback
DEV_B2B_ALLOWED_RP_IDS=localhost
