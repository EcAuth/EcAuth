using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using MockOpenIdProvider.Services;

namespace MockOpenIdProvider.Models
{
    public class IdpDbContext : DbContext
    {
        private readonly IOrganizationService _organizationService;

        public IdpDbContext(DbContextOptions<IdpDbContext> options, IOrganizationService organizationService) : base(options)
        {
            _organizationService = organizationService;
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Client> Clients { get; set; }
        public DbSet<AuthorizationCode> AuthorizationCodes { get; set; }
        public DbSet<AccessToken> AccessTokens { get; set; }
        public DbSet<MockIdpUser> Users { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // MockIdpUserにグローバルクエリフィルターを適用
            modelBuilder.Entity<MockIdpUser>()
                .HasQueryFilter(u => u.Organization != null && u.Organization.TenantName == _organizationService.TenantName);

            // Clientにグローバルクエリフィルターを適用
            modelBuilder.Entity<Client>()
                .HasQueryFilter(c => c.Organization != null && c.Organization.TenantName == _organizationService.TenantName);

            // AuthorizationCodeにグローバルクエリフィルターを適用
            modelBuilder.Entity<AuthorizationCode>()
                .HasQueryFilter(ac => ac.Organization != null && ac.Organization.TenantName == _organizationService.TenantName);

            // AccessTokenにグローバルクエリフィルターを適用
            modelBuilder.Entity<AccessToken>()
                .HasQueryFilter(at => at.Organization != null && at.Organization.TenantName == _organizationService.TenantName);

            // RefreshTokenにグローバルクエリフィルターを適用
            modelBuilder.Entity<RefreshToken>()
                .HasQueryFilter(rt => rt.Organization != null && rt.Organization.TenantName == _organizationService.TenantName);

            // リレーション設定
            modelBuilder.Entity<MockIdpUser>()
                .HasOne(u => u.Organization)
                .WithMany(o => o.Users)
                .HasForeignKey(u => u.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Client>()
                .HasOne(c => c.Organization)
                .WithMany(o => o.Clients)
                .HasForeignKey(c => c.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AuthorizationCode>()
                .HasOne(ac => ac.Organization)
                .WithMany(o => o.AuthorizationCodes)
                .HasForeignKey(ac => ac.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AccessToken>()
                .HasOne(at => at.Organization)
                .WithMany(o => o.AccessTokens)
                .HasForeignKey(at => at.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<RefreshToken>()
                .HasOne(rt => rt.Organization)
                .WithMany(o => o.RefreshTokens)
                .HasForeignKey(rt => rt.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // インデックス設定
            modelBuilder.Entity<Organization>()
                .HasIndex(o => o.TenantName)
                .IsUnique();

            modelBuilder.Entity<Client>()
                .HasIndex(c => new { c.OrganizationId, c.ClientId })
                .IsUnique();

            base.OnModelCreating(modelBuilder);
        }
    }
}
