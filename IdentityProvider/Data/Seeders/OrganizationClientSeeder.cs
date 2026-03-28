using System.Security.Cryptography;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data.Seeders;

/// <summary>
/// 環境固有の Organization/Client データを投入するシーダー。
/// - Organization 作成
/// - Client 作成（Organization に紐付け）
/// - RedirectUri 作成
/// - RsaKeyPair 生成・保存
/// - OpenIdProvider（MockIdP）作成
/// </summary>
public class OrganizationClientSeeder : IDbSeeder
{
    /// <inheritdoc />
    public string RequiredMigration => "20260328224428_ChangeRsaKeyPairToOrganization";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async Task SeedAsync(
        EcAuthDbContext context,
        IConfiguration configuration,
        ILogger logger)
    {
        var prefix = GetEnvironmentPrefix(configuration);
        logger.LogInformation("Using environment prefix {Prefix}", prefix);

        // 環境変数から値を取得
        var organizationCode = GetConfigValue(configuration, prefix, "ORGANIZATION_CODE");
        var organizationName = GetConfigValue(configuration, prefix, "ORGANIZATION_NAME");
        var organizationTenantName = GetConfigValue(configuration, prefix, "ORGANIZATION_TENANT_NAME");
        var clientId = GetConfigValue(configuration, prefix, "CLIENT_ID");
        var clientSecret = GetConfigValue(configuration, prefix, "CLIENT_SECRET");
        var appName = GetConfigValue(configuration, prefix, "APP_NAME");
        var redirectUri = GetConfigValue(configuration, prefix, "REDIRECT_URI");

        // MockIdP 設定
        var mockIdpAppName = GetConfigValue(configuration, prefix, "MOCK_IDP_APP_NAME");
        var mockIdpClientId = GetConfigValue(configuration, prefix, "MOCK_IDP_CLIENT_ID");
        var mockIdpClientSecret = GetConfigValue(configuration, prefix, "MOCK_IDP_CLIENT_SECRET");
        var mockIdpAuthorizationEndpoint = GetConfigValue(configuration, prefix, "MOCK_IDP_AUTHORIZATION_ENDPOINT");
        var mockIdpTokenEndpoint = GetConfigValue(configuration, prefix, "MOCK_IDP_TOKEN_ENDPOINT");
        var mockIdpUserinfoEndpoint = GetConfigValue(configuration, prefix, "MOCK_IDP_USERINFO_ENDPOINT");

        // 必須パラメータのチェック
        if (string.IsNullOrEmpty(organizationCode))
        {
            logger.LogInformation("Skipped - Organization code not configured for prefix {Prefix}", prefix);
            return;
        }

        if (string.IsNullOrEmpty(clientId))
        {
            logger.LogInformation("Skipped - Client ID not configured for prefix {Prefix}", prefix);
            return;
        }

        var hasChanges = false;

        // 1. Organization 作成
        var organization = await SeedOrganizationAsync(
            context, organizationCode, organizationName, organizationTenantName, logger);

        // 2. Client 作成
        var client = await SeedClientAsync(
            context, clientId, clientSecret, appName, organization, logger);
        hasChanges |= client.created;

        if (client.entity == null)
        {
            logger.LogWarning("Skipped remaining seeds - Client could not be created or found");
            return;
        }

        // 3. RedirectUri 作成
        hasChanges |= await SeedRedirectUriAsync(context, client.entity, redirectUri, logger);

        // 4. RsaKeyPair 生成・保存（Organization単位）
        hasChanges |= await SeedRsaKeyPairAsync(context, organization, logger);

        // 5. OpenIdProvider（MockIdP）作成
        hasChanges |= await SeedOpenIdProviderAsync(
            context, client.entity,
            mockIdpAppName, mockIdpClientId, mockIdpClientSecret,
            mockIdpAuthorizationEndpoint, mockIdpTokenEndpoint, mockIdpUserinfoEndpoint,
            logger);

        if (hasChanges)
        {
            await context.SaveChangesAsync();
            logger.LogInformation("Seed data saved successfully");
        }
        else
        {
            logger.LogInformation("No changes needed");
        }
    }

    private static async Task<Organization> SeedOrganizationAsync(
        EcAuthDbContext context,
        string organizationCode,
        string? organizationName,
        string? organizationTenantName,
        ILogger logger)
    {
        var existing = await context.Organizations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Code == organizationCode);

        if (existing != null)
        {
            logger.LogInformation("Organization {Code} already exists, skipping", organizationCode);
            return existing;
        }

        var organization = new Organization
        {
            Code = organizationCode,
            Name = organizationName ?? organizationCode,
            TenantName = organizationTenantName ?? organizationCode
        };

        context.Organizations.Add(organization);
        await context.SaveChangesAsync();

        logger.LogInformation("Created Organization {Code}", organizationCode);
        return organization;
    }

    private static async Task<(Client? entity, bool created)> SeedClientAsync(
        EcAuthDbContext context,
        string clientId,
        string? clientSecret,
        string? appName,
        Organization organization,
        ILogger logger)
    {
        var existing = await context.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ClientId == clientId);

        if (existing != null)
        {
            logger.LogInformation("Client {ClientId} already exists, skipping", clientId);
            return (existing, false);
        }

        if (string.IsNullOrEmpty(clientSecret))
        {
            logger.LogWarning("Client creation skipped - Client secret not configured");
            return (null, false);
        }

        var client = new Client
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            AppName = appName ?? clientId,
            OrganizationId = organization.Id
        };

        context.Clients.Add(client);
        await context.SaveChangesAsync();

        logger.LogInformation("Created Client {ClientId} for Organization {OrgCode}",
            clientId, organization.Code);
        return (client, true);
    }

    private static async Task<bool> SeedRedirectUriAsync(
        EcAuthDbContext context,
        Client client,
        string? redirectUri,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(redirectUri))
        {
            return false;
        }

        var exists = await context.RedirectUris
            .IgnoreQueryFilters()
            .AnyAsync(r => r.Uri == redirectUri && r.ClientId == client.Id);

        if (exists)
        {
            return false;
        }

        context.RedirectUris.Add(new RedirectUri
        {
            Uri = redirectUri,
            ClientId = client.Id
        });

        logger.LogInformation("Added RedirectUri {Uri} for client {ClientId}",
            redirectUri, client.ClientId);
        return true;
    }

    private static async Task<bool> SeedRsaKeyPairAsync(
        EcAuthDbContext context,
        Organization organization,
        ILogger logger)
    {
        var exists = await context.RsaKeyPairs
            .IgnoreQueryFilters()
            .AnyAsync(r => r.OrganizationId == organization.Id);

        if (exists)
        {
            return false;
        }

        using var rsa = RSA.Create(2048);
        var publicKeyBase64 = Convert.ToBase64String(rsa.ExportRSAPublicKey());
        var privateKeyBase64 = Convert.ToBase64String(rsa.ExportRSAPrivateKey());

        context.RsaKeyPairs.Add(new RsaKeyPair
        {
            Kid = Guid.NewGuid().ToString(),
            OrganizationId = organization.Id,
            PublicKey = publicKeyBase64,
            PrivateKey = privateKeyBase64,
            IsActive = true
        });

        logger.LogInformation("Generated RsaKeyPair for organization {OrganizationCode}", organization.Code);
        return true;
    }

    private static async Task<bool> SeedOpenIdProviderAsync(
        EcAuthDbContext context,
        Client client,
        string? appName,
        string? idpClientId,
        string? idpClientSecret,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? userinfoEndpoint,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(idpClientId))
        {
            logger.LogInformation("Skipped OpenIdProvider - MockIdP settings not configured");
            return false;
        }

        var exists = await context.OpenIdProviders
            .IgnoreQueryFilters()
            .AnyAsync(o => o.Name == appName && o.ClientId == client.Id);

        if (exists)
        {
            return false;
        }

        context.OpenIdProviders.Add(new OpenIdProvider
        {
            Name = appName,
            IdpClientId = idpClientId,
            IdpClientSecret = idpClientSecret ?? string.Empty,
            AuthorizationEndpoint = authorizationEndpoint,
            TokenEndpoint = tokenEndpoint,
            UserinfoEndpoint = userinfoEndpoint,
            Client = client
        });

        logger.LogInformation("Created OpenIdProvider {Name} for client {ClientId}",
            appName, client.ClientId);
        return true;
    }

    private static string? GetConfigValue(IConfiguration configuration, string prefix, string key)
    {
        return prefix == "DEV"
            ? configuration[$"DEFAULT_{key}"]
            : configuration[$"{prefix}_{key}"];
    }

    private static string GetEnvironmentPrefix(IConfiguration configuration)
    {
        var env = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Development";

        return env switch
        {
            "Development" => "DEV",
            "Staging" => "STAGING",
            "Production" => "PROD",
            _ => "DEV"
        };
    }
}
