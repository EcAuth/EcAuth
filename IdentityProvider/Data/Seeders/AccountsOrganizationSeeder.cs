using System.Security.Cryptography;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Data.Seeders;

/// <summary>
/// EcAuth Account 管理用の <c>accounts</c> / <c>stg-accounts</c> Organization と
/// それぞれの管理コンソール Client（<see cref="SubjectType.Account"/>）を投入するシーダー。
/// <para>
/// 既存の <see cref="OrganizationClientSeeder"/> が ASPNETCORE_ENVIRONMENT に応じた
/// DEV/STAGING/PROD プレフィックスで分岐するのに対し、本シーダーは環境に依存せず
/// 固定の <c>ACCOUNTS_*</c> / <c>STG_ACCOUNTS_*</c> 環境変数の有無で投入可否を判定する。
/// 本番 App Service では Terraform app_settings から両プレフィックスが注入され 2 Org が投入される。
/// ローカル開発では <c>.env.dev.tpl</c> のダミー値で動作確認できる。
/// staging では設定しないため何も投入されずスキップされる（Account 機能は本番のみ）。
/// </para>
/// </summary>
public class AccountsOrganizationSeeder : IDbSeeder
{
    /// <inheritdoc />
    /// <remarks>
    /// Phase A で 4 マイグレーションが統合されたため、<c>AddSubjectTypeToClient</c> を含む
    /// 統合マイグレーション名を指定する。
    /// </remarks>
    public string RequiredMigration => "20260514085625_AddAccountManagementSchema";

    /// <inheritdoc />
    public int Order => 20;

    /// <summary>
    /// 投入対象の Organization 定義。Organization の <c>code</c> / <c>tenant_name</c> は
    /// サブドメインからのテナント解決と一致させる必要があるため固定値とする。
    /// </summary>
    private static readonly AccountOrgDefinition[] Definitions =
    {
        new(
            ConfigPrefix: "ACCOUNTS",
            Code: "accounts",
            Name: "EcAuth Accounts",
            TenantName: "accounts"),
        new(
            ConfigPrefix: "STG_ACCOUNTS",
            Code: "stg-accounts",
            Name: "EcAuth Accounts (Staging)",
            TenantName: "stg-accounts"),
    };

    /// <inheritdoc />
    public async Task SeedAsync(
        EcAuthDbContext context,
        IConfiguration configuration,
        ILogger logger)
    {
        var hasChanges = false;

        foreach (var definition in Definitions)
        {
            hasChanges |= await SeedDefinitionAsync(context, configuration, definition, logger);
        }

        if (hasChanges)
        {
            await context.SaveChangesAsync();
            logger.LogInformation("Account organization seed data saved successfully");
        }
        else
        {
            logger.LogInformation("No changes needed for account organizations");
        }
    }

    private static async Task<bool> SeedDefinitionAsync(
        EcAuthDbContext context,
        IConfiguration configuration,
        AccountOrgDefinition definition,
        ILogger logger)
    {
        var clientId = configuration[$"{definition.ConfigPrefix}_CLIENT_ID"];
        var clientSecret = configuration[$"{definition.ConfigPrefix}_CLIENT_SECRET"];
        var allowedRpIds = configuration[$"{definition.ConfigPrefix}_ALLOWED_RP_IDS"];
        var redirectUri = configuration[$"{definition.ConfigPrefix}_REDIRECT_URI"];

        // Client ID が未設定の環境（staging 等）ではこの Org を投入しない
        // 空白のみの値も未設定とみなす（誤設定による不正な Client ID 投入を防ぐ）
        if (string.IsNullOrWhiteSpace(clientId))
        {
            logger.LogInformation(
                "Skipped {Code} - {Prefix}_CLIENT_ID not configured",
                definition.Code, definition.ConfigPrefix);
            return false;
        }

        var hasChanges = false;

        // 1. Organization 作成
        var organization = await SeedOrganizationAsync(context, definition, logger);

        // 2. Client 作成（SubjectType.Account）
        var client = await SeedClientAsync(
            context, definition, clientId, clientSecret, allowedRpIds, organization, logger);
        hasChanges |= client.created;

        if (client.entity == null)
        {
            logger.LogWarning(
                "Skipped remaining seeds for {Code} - Client could not be created or found",
                definition.Code);
            return hasChanges;
        }

        // 3. RedirectUri 作成
        hasChanges |= await SeedRedirectUriAsync(context, client.entity, redirectUri, logger);

        // 4. RsaKeyPair 生成・保存（Organization 単位）
        hasChanges |= await SeedRsaKeyPairAsync(context, organization, logger);

        return hasChanges;
    }

    private static async Task<Organization> SeedOrganizationAsync(
        EcAuthDbContext context,
        AccountOrgDefinition definition,
        ILogger logger)
    {
        var existing = await context.Organizations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Code == definition.Code);

        if (existing != null)
        {
            logger.LogInformation("Organization {Code} already exists, skipping", definition.Code);
            return existing;
        }

        var organization = new Organization
        {
            Code = definition.Code,
            Name = definition.Name,
            TenantName = definition.TenantName
        };

        context.Organizations.Add(organization);
        await context.SaveChangesAsync();

        logger.LogInformation("Created Organization {Code}", definition.Code);
        return organization;
    }

    private static async Task<(Client? entity, bool created)> SeedClientAsync(
        EcAuthDbContext context,
        AccountOrgDefinition definition,
        string clientId,
        string? clientSecret,
        string? allowedRpIds,
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

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogWarning(
                "Client creation skipped for {Code} - {Prefix}_CLIENT_SECRET not configured",
                definition.Code, definition.ConfigPrefix);
            return (null, false);
        }

        var client = new Client
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            AppName = definition.Name,
            OrganizationId = organization.Id,
            SubjectType = SubjectType.Account
        };

        if (!string.IsNullOrWhiteSpace(allowedRpIds))
        {
            client.AllowedRpIds = allowedRpIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();
        }

        context.Clients.Add(client);
        await context.SaveChangesAsync();

        logger.LogInformation("Created Account Client {ClientId} for Organization {OrgCode}",
            clientId, organization.Code);
        return (client, true);
    }

    private static async Task<bool> SeedRedirectUriAsync(
        EcAuthDbContext context,
        Client client,
        string? redirectUri,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
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

    /// <summary>
    /// 固定の Organization 定義と、対応する環境変数プレフィックスの組。
    /// </summary>
    private sealed record AccountOrgDefinition(
        string ConfigPrefix,
        string Code,
        string Name,
        string TenantName);
}
