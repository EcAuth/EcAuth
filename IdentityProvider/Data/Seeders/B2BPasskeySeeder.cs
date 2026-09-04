using IdentityProvider.Models;
using IdentityProvider.Services;
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
    // b2b_user_identity へ書き込むため、identity 表を作るマイグレーションでゲートする
    // （EcAuthDocs#110）。DbInitializer は RequiredMigration が適用済みなら本シーダーを走らせるため、
    // 旧マイグレーション名のままだと、マイグレーションがデプロイに遅れた環境で
    // 表が存在しないまま INSERT に到達し、起動初期化が invalid object name で落ちる。
    public string RequiredMigration => "20260828230716_AddB2BUserIdentity";

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

        // 3. B2BUser を作成（ExternalId が設定されている場合のみ）
        if (string.IsNullOrEmpty(b2bUserExternalId))
        {
            logger.LogInformation("Skipped B2BUser creation - {Prefix}_B2B_USER_EXTERNAL_ID not configured", prefix);
        }
        else
        {
            hasChanges |= await SeedB2BUserAsync(
                context, client, b2bUserSubject, b2bUserExternalId, organizationCode, logger);
        }

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

    private static Task<bool> SeedAllowedRpIdsAsync(
        EcAuthDbContext context,
        Client client,
        string? b2bAllowedRpIds,
        string clientId,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(b2bAllowedRpIds))
        {
            return Task.FromResult(false);
        }

        // カンマ区切りの文字列を分割し、空白をトリムして空文字を除外
        var rpIdsToAdd = b2bAllowedRpIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var currentRpIds = client.AllowedRpIds;
        var hasChanges = false;

        foreach (var rpId in rpIdsToAdd)
        {
            if (currentRpIds.Contains(rpId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            currentRpIds.Add(rpId);
            logger.LogInformation("Added {RpId} to AllowedRpIds for client {ClientId}",
                rpId, clientId);
            hasChanges = true;
        }

        if (hasChanges)
        {
            client.AllowedRpIds = currentRpIds;
            client.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(hasChanges);
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
        Client client,
        string b2bUserSubject,
        string b2bUserExternalId,
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

        // external_id は個人情報を含み得るため、書き込み経路と同じく正規化 + ハッシュ化して保持する。
        // 環境変数 {prefix}_B2B_USER_EXTERNAL_ID には従来どおり平文 login_id を設定する。
        var externalIdHash = ExternalIdHasher.Hash(b2bUserExternalId);

        context.B2BUsers.Add(new B2BUser
        {
            Subject = b2bUserSubject,
            ExternalId = externalIdHash,
            UserType = "admin",
            OrganizationId = organization.Id
        });

        // 発行元ごとの識別子（EcAuthDocs#110）。発行元は「構成された Client」そのものでなければ
        // ならない。B2BPasskeyService は認証時に request.client_id から解決した Client で
        // IssuerKey を組み立てるため、ここで Organization 内の別 Client（最初に見つかった B2B
        // Client 等）を選ぶと identity 検索が外れ、毎回フォールバック経路に落ちる。
        if (client.SubjectType != SubjectType.B2B)
        {
            // identity 無しでも b2b_user.external_id 経由のフォールバックで解決できるため、
            // シード自体は続行する（移行前データと同じ状態になる）。
            logger.LogWarning(
                "B2BUserIdentity creation skipped - client {ClientId} is not a B2B client (SubjectType={SubjectType})",
                client.ClientId, client.SubjectType);
        }
        else if (client.OrganizationId != organization.Id)
        {
            // 別 Organization の Client を発行元にすると、B2BPasskeyService の Organization
            // 境界チェックで弾かれる identity を作ってしまう。
            logger.LogWarning(
                "B2BUserIdentity creation skipped - client {ClientId} does not belong to organization {OrgCode}",
                client.ClientId, organizationCode);
        }
        else
        {
            context.B2BUserIdentities.Add(new B2BUserIdentity
            {
                B2BSubject = b2bUserSubject,
                IssuerKey = B2BIssuerKey.ForClient(client.ClientId),
                ExternalId = externalIdHash,
                ClientId = client.ClientId
            });
        }

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
