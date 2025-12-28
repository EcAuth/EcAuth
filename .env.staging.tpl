# 1Password テンプレートファイル（staging 環境用）
# 使用方法: op inject -i .env.staging.tpl -o .env
#
# EcAuth DB: Azure SQL Database（1Password から取得）
# MockIdP: Azure staging 環境（1Password から取得）

# =============================================================================
# Database Configuration (Azure SQL Database)
# =============================================================================
# 接続文字列を個別フィールドから組み立てる場合は、以下を使用後にシェルで組み立て
SQL_HOST=op://EcAuth/ecauth-staging-sql/hostname
SQL_DATABASE=op://EcAuth/ecauth-staging-sql/database
SQL_USERNAME=op://EcAuth/ecauth-staging-sql/username
SQL_PASSWORD=op://EcAuth/ecauth-staging-sql/password

# 注: ConnectionStrings__EcAuthDbContext は GitHub Actions ワークフローで組み立てます
# export ConnectionStrings__EcAuthDbContext="Server=tcp:${SQL_HOST},1433;Initial Catalog=${SQL_DATABASE};User ID=${SQL_USERNAME};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# =============================================================================
# Staging Organization/Client Settings
# =============================================================================
STAGING_ORGANIZATION_CODE=op://EcAuth/ecauth-staging-app/organization_code
STAGING_ORGANIZATION_NAME=op://EcAuth/ecauth-staging-app/organization_name
STAGING_ORGANIZATION_TENANT_NAME=op://EcAuth/ecauth-staging-app/organization_tenant_name
STAGING_CLIENT_ID=op://EcAuth/ecauth-staging-app/client_id
STAGING_CLIENT_SECRET=op://EcAuth/ecauth-staging-app/client_secret
STAGING_APP_NAME=op://EcAuth/ecauth-staging-app/app_name
STAGING_REDIRECT_URI=op://EcAuth/ecauth-staging-app/redirect_uri

# =============================================================================
# Staging MockIdP Settings (Azure MockIdP との連携)
# =============================================================================
# EcAuth から Azure MockIdP への OAuth2 連携設定
STAGING_MOCK_IDP_APP_NAME=op://EcAuth/ecauth-staging-mockidp/app_name
STAGING_MOCK_IDP_CLIENT_ID=op://EcAuth/ecauth-staging-mockidp/client_id
STAGING_MOCK_IDP_CLIENT_SECRET=op://EcAuth/ecauth-staging-mockidp/client_secret

# MockIdP エンドポイント（Azure Container Apps）
MOCK_IDP_BASE_URL=op://EcAuth/mockidp-staging/base_url
STAGING_MOCK_IDP_AUTHORIZATION_ENDPOINT=op://EcAuth/mockidp-staging/authorization_endpoint
STAGING_MOCK_IDP_TOKEN_ENDPOINT=op://EcAuth/mockidp-staging/token_endpoint
STAGING_MOCK_IDP_USERINFO_ENDPOINT=op://EcAuth/mockidp-staging/userinfo_endpoint

# =============================================================================
# EC-CUBE2 Plugin Client Settings
# =============================================================================
ECCUBE2_CLIENT_ID=op://EcAuth/eccube2-ecauth-plugin/client_id
ECCUBE2_CLIENT_SECRET=op://EcAuth/eccube2-ecauth-plugin/client_secret
ECCUBE2_REDIRECT_URI=op://EcAuth/eccube2-ecauth-plugin/redirect_uri
