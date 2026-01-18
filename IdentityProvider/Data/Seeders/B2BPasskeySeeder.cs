using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data.Seeders;

/// <summary>
/// B2Bパスキー関連のシードデータを投入するシーダー。
/// - Client に AllowedRpIds を設定
/// - B2Bパスキーテスト用 RedirectUri を追加
/// - テスト用 B2BUser を作成
/// </summary>
public class B2BPasskeySeeder : IDbSeeder
{
    /// <inheritdoc />
    public string RequiredMigration => "20260111034146_AddB2BPasskeyEntities";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async Task SeedAsync(
        EcAuthDbContext context,
        IConfiguration configuration,
        ILogger logger)
    {
        // 環境に応じた設定キーのプレフィックスを決定
        var prefix = GetEnvironmentPrefix(configuration);
        logger.LogInformation("Using environment prefix {Prefix}", prefix);

        // 環境変数から値を取得
        var clientId = prefix == "DEV"
            ? configuration["DEFAULT_CLIENT_ID"]
            : configuration[$"{prefix}_CLIENT_ID"];
        var organizationCode = prefix == "DEV"
            ? configuration["DEFAULT_ORGANIZATION_CODE"]
            : configuration[$"{prefix}_ORGANIZATION_CODE"];
        var b2bUserSubject = configuration[$"{prefix}_B2B_USER_SUBJECT"];
        var b2bUserExternalId = configuration[$"{prefix}_B2B_USER_EXTERNAL_ID"];
        var b2bRedirectUri = configuration[$"{prefix}_B2B_REDIRECT_URI"];
        var b2bAllowedRpIds = configuration[$"{prefix}_B2B_ALLOWED_RP_IDS"];

        // 必須パラメータのチェック
        if (string.IsNullOrEmpty(b2bUserSubject))
        {
            logger.LogInformation("Skipped - {Prefix}_B2B_USER_SUBJECT not configured", prefix);
            return;
        }

        if (string.IsNullOrEmpty(clientId))
        {
            logger.LogWarning("Skipped - Client ID not configured for prefix {Prefix}", prefix);
            return;
        }

        // テナントフィルターを無視してクライアントを取得
        var client = await context.Clients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ClientId == clientId);

        if (client == null)
        {
            logger.LogWarning("Skipped - Client {ClientId} not found", clientId);
            return;
        }

        var hasChanges = false;

        // 1. Client に AllowedRpIds を設定
        hasChanges |= await SeedAllowedRpIdsAsync(context, client, b2bAllowedRpIds, clientId, logger);

        // 2. RedirectUri を追加
        hasChanges |= await SeedRedirectUriAsync(context, client, b2bRedirectUri, clientId, logger);

        // 3. B2BUser を作成
        hasChanges |= await SeedB2BUserAsync(context, b2bUserSubject, b2bUserExternalId, organizationCode, logger);

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

    private static async Task<bool> SeedAllowedRpIdsAsync(
        EcAuthDbContext context,
        Client client,
        string? b2bAllowedRpIds,
        string clientId,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(b2bAllowedRpIds))
        {
            return false;
        }

        var currentRpIds = client.AllowedRpIds;
        if (currentRpIds.Contains(b2bAllowedRpIds))
        {
            return false;
        }

        currentRpIds.Add(b2bAllowedRpIds);
        client.AllowedRpIds = currentRpIds;
        client.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Added {RpId} to AllowedRpIds for client {ClientId}",
            b2bAllowedRpIds, clientId);

        return true;
    }

    private static async Task<bool> SeedRedirectUriAsync(
        EcAuthDbContext context,
        Client client,
        string? b2bRedirectUri,
        string clientId,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(b2bRedirectUri))
        {
            return false;
        }

        var existingUri = await context.RedirectUris
            .IgnoreQueryFilters()
            .AnyAsync(r => r.Uri == b2bRedirectUri && r.ClientId == client.Id);

        if (existingUri)
        {
            return false;
        }

        context.RedirectUris.Add(new RedirectUri
        {
            Uri = b2bRedirectUri,
            ClientId = client.Id
        });

        logger.LogInformation("Added RedirectUri {Uri} for client {ClientId}",
            b2bRedirectUri, clientId);

        return true;
    }

    private static async Task<bool> SeedB2BUserAsync(
        EcAuthDbContext context,
        string b2bUserSubject,
        string? b2bUserExternalId,
        string? organizationCode,
        ILogger logger)
    {
        var existingUser = await context.B2BUsers
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Subject == b2bUserSubject);

        if (existingUser)
        {
            return false;
        }

        var organization = await context.Organizations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Code == organizationCode);

        if (organization == null)
        {
            logger.LogWarning("B2BUser creation skipped - Organization {OrgCode} not found",
                organizationCode);
            return false;
        }

        context.B2BUsers.Add(new B2BUser
        {
            Subject = b2bUserSubject,
            ExternalId = b2bUserExternalId,
            UserType = "admin",
            OrganizationId = organization.Id
        });

        logger.LogInformation("Created B2BUser {Subject} for organization {OrgCode}",
            b2bUserSubject, organizationCode);

        return true;
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
