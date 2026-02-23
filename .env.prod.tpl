# 1Password テンプレートファイル（production 環境用）
# 使用方法: op inject -i .env.prod.tpl -o .env
#
# EcAuth DB: Azure SQL Database（1Password から取得）
# MockIdP: Azure production 環境（1Password から取得）

# =============================================================================
# Database Configuration (Azure SQL Database)
# =============================================================================
# 接続文字列を個別フィールドから組み立てる場合は、以下を使用後にシェルで組み立て
SQL_HOST=op://EcAuth/ecauth-prod-sql/hostname
SQL_DATABASE=op://EcAuth/ecauth-prod-sql/database
SQL_USERNAME=op://EcAuth/ecauth-prod-sql/username
SQL_PASSWORD=op://EcAuth/ecauth-prod-sql/password

# 注: ConnectionStrings__EcAuthDbContext は GitHub Actions ワークフローで組み立てます
# export ConnectionStrings__EcAuthDbContext="Server=tcp:${SQL_HOST},1433;Initial Catalog=${SQL_DATABASE};User ID=${SQL_USERNAME};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# =============================================================================
# Production Organization/Client Settings
# =============================================================================
PROD_ORGANIZATION_CODE=op://EcAuth/ecauth-prod-app/organization_code
PROD_ORGANIZATION_NAME=op://EcAuth/ecauth-prod-app/organization_name
PROD_ORGANIZATION_TENANT_NAME=op://EcAuth/ecauth-prod-app/organization_tenant_name
PROD_CLIENT_ID=op://EcAuth/ecauth-prod-app/client_id
PROD_CLIENT_SECRET=op://EcAuth/ecauth-prod-app/client_secret
PROD_APP_NAME=op://EcAuth/ecauth-prod-app/app_name
PROD_REDIRECT_URI=op://EcAuth/ecauth-prod-app/redirect_uri

# =============================================================================
# Production MockIdP Settings (Azure MockIdP との連携)
# =============================================================================
# EcAuth から Azure MockIdP への OAuth2 連携設定
PROD_MOCK_IDP_APP_NAME=op://EcAuth/ecauth-prod-mockidp/app_name
PROD_MOCK_IDP_CLIENT_ID=op://EcAuth/ecauth-prod-mockidp/client_id
PROD_MOCK_IDP_CLIENT_SECRET=op://EcAuth/ecauth-prod-mockidp/client_secret

# MockIdP エンドポイント（Azure Container Apps）
MOCK_IDP_BASE_URL=op://EcAuth/mockidp-production/base_url
PROD_MOCK_IDP_AUTHORIZATION_ENDPOINT=op://EcAuth/mockidp-production/authorization_endpoint
PROD_MOCK_IDP_TOKEN_ENDPOINT=op://EcAuth/mockidp-production/token_endpoint
PROD_MOCK_IDP_USERINFO_ENDPOINT=op://EcAuth/mockidp-production/userinfo_endpoint

# =============================================================================
# B2B Passkey Data (Production)
# =============================================================================
PROD_B2B_USER_SUBJECT=op://EcAuth/ecauth-prod-app/b2b_user_subject
PROD_B2B_USER_EXTERNAL_ID=op://EcAuth/ecauth-prod-app/b2b_user_external_id
PROD_B2B_REDIRECT_URI=op://EcAuth/ecauth-prod-app/b2b_redirect_uri
PROD_B2B_ALLOWED_RP_IDS=op://EcAuth/ecauth-prod-app/b2b_allowed_rp_ids
