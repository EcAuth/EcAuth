using System.ComponentModel;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Models
{
    public class EcAuthDbContext : DbContext
    {
        private readonly ITenantService _tenantService;

        public EcAuthDbContext(DbContextOptions<EcAuthDbContext> options, ITenantService tenantService) : base(options)
        {
            _tenantService = tenantService;
        }

        public DbSet<Client> Clients { get; set; }
        public DbSet<Account> Accounts { get; set; }
        public DbSet<Organization> Organizations { get; set; }
        public DbSet<RsaKeyPair> RsaKeyPairs { get; set; }
        public DbSet<RedirectUri> RedirectUris { get; set; }
        public DbSet<OpenIdProvider> OpenIdProviders { get; set; }
        public DbSet<OpenIdProviderScope> OpenIdProviderScopes { get; set; }
        public DbSet<EcAuthUser> EcAuthUsers { get; set; }
        public DbSet<ExternalIdpMapping> ExternalIdpMappings { get; set; }
        public DbSet<AuthorizationCode> AuthorizationCodes { get; set; }
        public DbSet<AccessToken> AccessTokens { get; set; }
        public DbSet<ExternalIdpToken> ExternalIdpTokens { get; set; }
        public DbSet<B2BUser> B2BUsers { get; set; }
        public DbSet<B2BPasskeyCredential> B2BPasskeyCredentials { get; set; }
        public DbSet<WebAuthnChallenge> WebAuthnChallenges { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // 組織エンティティにテナントフィルターを適用
            modelBuilder.Entity<Organization>()
                .HasQueryFilter(o => o.TenantName == _tenantService.TenantName);

            // EcAuthUserにも同じグローバルクエリフィルターを適用
            modelBuilder.Entity<EcAuthUser>()
                .HasQueryFilter(u => u.Organization != null && u.Organization.TenantName == _tenantService.TenantName);

            // ExternalIdpMappingにもグローバルクエリフィルターを適用
            modelBuilder.Entity<ExternalIdpMapping>()
                .HasQueryFilter(m => m.EcAuthUser != null && m.EcAuthUser.Organization != null && m.EcAuthUser.Organization.TenantName == _tenantService.TenantName);

            // AuthorizationCodeにもグローバルクエリフィルターを適用
            // 注: 旧外部キー削除後はClient経由でテナントフィルターを適用
            modelBuilder.Entity<AuthorizationCode>()
                .HasQueryFilter(ac => ac.Client != null && ac.Client.Organization != null && ac.Client.Organization.TenantName == _tenantService.TenantName);

            // AccessTokenにもグローバルクエリフィルターを適用
            // 注: 旧外部キー削除後はClient経由でテナントフィルターを適用
            modelBuilder.Entity<AccessToken>()
                .HasQueryFilter(at => at.Client != null && at.Client.Organization != null && at.Client.Organization.TenantName == _tenantService.TenantName);

            // ExternalIdpTokenにもグローバルクエリフィルターを適用
            modelBuilder.Entity<ExternalIdpToken>()
                .HasQueryFilter(eit => eit.EcAuthUser != null && eit.EcAuthUser.Organization != null && eit.EcAuthUser.Organization.TenantName == _tenantService.TenantName);

            // EcAuthUser関連の設定
            modelBuilder.Entity<EcAuthUser>()
                .HasOne(u => u.Organization)
                .WithMany()
                .HasForeignKey(u => u.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // SubjectをユニークなAlternate Keyとして設定
            modelBuilder.Entity<EcAuthUser>()
                .HasAlternateKey(u => u.Subject);

            modelBuilder.Entity<EcAuthUser>()
                .HasMany(u => u.ExternalIdpMappings)
                .WithOne(m => m.EcAuthUser)
                .HasForeignKey(m => m.EcAuthSubject)
                .HasPrincipalKey(u => u.Subject)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EcAuthUser>()
                .HasMany<ExternalIdpToken>()
                .WithOne(eit => eit.EcAuthUser)
                .HasForeignKey(eit => eit.EcAuthSubject)
                .HasPrincipalKey(u => u.Subject)
                .OnDelete(DeleteBehavior.Cascade);

            // AuthorizationCode関連の設定
            modelBuilder.Entity<AuthorizationCode>()
                .HasOne(ac => ac.Client)
                .WithMany()
                .HasForeignKey(ac => ac.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            // AccessToken関連の設定
            modelBuilder.Entity<AccessToken>()
                .HasOne(at => at.Client)
                .WithMany()
                .HasForeignKey(at => at.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            // インデックスの設定
            modelBuilder.Entity<EcAuthUser>()
                .HasIndex(u => new { u.OrganizationId, u.EmailHash })
                .IsUnique();

            modelBuilder.Entity<EcAuthUser>()
                .HasIndex(u => u.Subject)
                .IsUnique();

            modelBuilder.Entity<ExternalIdpMapping>()
                .HasIndex(m => new { m.ExternalProvider, m.ExternalSubject })
                .IsUnique();

            modelBuilder.Entity<AuthorizationCode>()
                .HasIndex(ac => ac.ExpiresAt);

            modelBuilder.Entity<AccessToken>()
                .HasIndex(at => at.Token)
                .IsUnique();

            modelBuilder.Entity<AccessToken>()
                .HasIndex(at => at.ExpiresAt);

            modelBuilder.Entity<ExternalIdpToken>()
                .HasIndex(eit => new { eit.EcAuthSubject, eit.ExternalProvider })
                .IsUnique();

            modelBuilder.Entity<ExternalIdpToken>()
                .HasIndex(eit => eit.ExpiresAt);

            // B2BUser テナントフィルター
            modelBuilder.Entity<B2BUser>()
                .HasQueryFilter(u => u.Organization != null && u.Organization.TenantName == _tenantService.TenantName);

            // B2BPasskeyCredential テナントフィルター（B2BUser経由）
            modelBuilder.Entity<B2BPasskeyCredential>()
                .HasQueryFilter(c => c.B2BUser != null && c.B2BUser.Organization != null && c.B2BUser.Organization.TenantName == _tenantService.TenantName);

            // WebAuthnChallenge テナントフィルター（Client経由）
            modelBuilder.Entity<WebAuthnChallenge>()
                .HasQueryFilter(wc => wc.Client != null && wc.Client.Organization != null && wc.Client.Organization.TenantName == _tenantService.TenantName);

            // B2BUser 関連の設定
            modelBuilder.Entity<B2BUser>()
                .HasAlternateKey(u => u.Subject);

            modelBuilder.Entity<B2BUser>()
                .HasOne(u => u.Organization)
                .WithMany()
                .HasForeignKey(u => u.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<B2BUser>()
                .HasMany(u => u.PasskeyCredentials)
                .WithOne(c => c.B2BUser)
                .HasForeignKey(c => c.B2BSubject)
                .HasPrincipalKey(u => u.Subject)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<B2BUser>()
                .HasIndex(u => new { u.OrganizationId, u.ExternalId })
                .IsUnique()
                .HasFilter("[external_id] IS NOT NULL");

            // B2BPasskeyCredential 関連の設定
            modelBuilder.Entity<B2BPasskeyCredential>()
                .HasIndex(c => c.CredentialId)
                .IsUnique();

            // WebAuthnChallenge 関連の設定
            modelBuilder.Entity<WebAuthnChallenge>()
                .HasIndex(wc => wc.SessionId)
                .IsUnique();

            modelBuilder.Entity<WebAuthnChallenge>()
                .HasIndex(wc => wc.ExpiresAt);

            modelBuilder.Entity<WebAuthnChallenge>()
                .HasOne(wc => wc.Client)
                .WithMany()
                .HasForeignKey(wc => wc.ClientId)
                .OnDelete(DeleteBehavior.Restrict);

            base.OnModelCreating(modelBuilder);
        }
    }
}
