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
        public DbSet<B2BUserIdentity> B2BUserIdentities { get; set; }
        public DbSet<B2BPasskeyCredential> B2BPasskeyCredentials { get; set; }
        public DbSet<WebAuthnChallenge> WebAuthnChallenges { get; set; }
        public DbSet<AccountOrganization> AccountOrganizations { get; set; }
        public DbSet<MagicLoginToken> MagicLoginTokens { get; set; }
        public DbSet<PasskeyRegistrationToken> PasskeyRegistrationTokens { get; set; }
        public DbSet<SignupRequest> SignupRequests { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // 組織エンティティにテナントフィルターを適用。
            //
            // 論理削除（DeletedAt）はテナント条件と同じ扱いで全フィルターに乗せる。EF Core の
            // グローバルクエリフィルターは「クエリのルート」と「Include したナビゲーション先」には
            // 効くが、下記のように述語の中でナビゲーションを辿った参照（c.Organization.XXX）には
            // 自動適用されない。したがって Organization 側に DeletedAt 条件を足すだけでは、
            // Client 経由・EcAuthUser 経由のエンティティが削除済み Org の行を拾い続ける。
            // 削除済みサイトで認証が通ってしまうため、派生フィルターにも同じ条件を明記する。
            modelBuilder.Entity<Organization>()
                .HasQueryFilter(o => o.TenantName == _tenantService.TenantName && o.DeletedAt == null);

            // Organization.Code のグローバルユニーク制約。
            // 論理削除しても code は解放されない（削除済み Org と同じコードで作り直せてしまうと、
            // 課金集計で別サイトの利用期間が同一コードに混ざるため）。削除済みドメインの
            // 再登録は AccountController が organization_deleted で明示的に拒否する。
            modelBuilder.Entity<Organization>()
                .HasIndex(o => o.Code)
                .IsUnique();

            // サンドボックス Org → 本番 Org の自己参照。
            // 自己参照のため Cascade は張れない（SQL Server が循環参照を拒否する）。
            modelBuilder.Entity<Organization>()
                .HasOne(o => o.ParentOrganization)
                .WithMany(o => o.SandboxOrganizations)
                .HasForeignKey(o => o.ParentOrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // 「1 本番 Org あたりサンドボックスは 1 つまで」を DB 制約として担保する。
            // 論理削除済みを除外しているのは、削除したサンドボックスの枠が空かないと
            // 「追加 → 動作確認 → 旧削除」での作り直しができなくなるため。
            modelBuilder.Entity<Organization>()
                .HasIndex(o => o.ParentOrganizationId)
                .IsUnique()
                .HasFilter("[parent_organization_id] IS NOT NULL AND [deleted_at] IS NULL")
                .HasDatabaseName("IX_organization_parent_organization_id_active");

            // EcAuthUserにも同じグローバルクエリフィルターを適用
            modelBuilder.Entity<EcAuthUser>()
                .HasQueryFilter(u => u.Organization != null && u.Organization.TenantName == _tenantService.TenantName && u.Organization.DeletedAt == null);

            // ExternalIdpMappingにもグローバルクエリフィルターを適用
            modelBuilder.Entity<ExternalIdpMapping>()
                .HasQueryFilter(m => m.EcAuthUser != null && m.EcAuthUser.Organization != null && m.EcAuthUser.Organization.TenantName == _tenantService.TenantName && m.EcAuthUser.Organization.DeletedAt == null);

            // AuthorizationCodeにもグローバルクエリフィルターを適用
            // 注: 旧外部キー削除後はClient経由でテナントフィルターを適用
            modelBuilder.Entity<AuthorizationCode>()
                .HasQueryFilter(ac => ac.Client != null && ac.Client.Organization != null && ac.Client.Organization.TenantName == _tenantService.TenantName && ac.Client.Organization.DeletedAt == null);

            // AccessTokenにもグローバルクエリフィルターを適用
            // 注: 旧外部キー削除後はClient経由でテナントフィルターを適用
            modelBuilder.Entity<AccessToken>()
                .HasQueryFilter(at => at.Client != null && at.Client.Organization != null && at.Client.Organization.TenantName == _tenantService.TenantName && at.Client.Organization.DeletedAt == null);

            // ExternalIdpTokenにもグローバルクエリフィルターを適用
            modelBuilder.Entity<ExternalIdpToken>()
                .HasQueryFilter(eit => eit.EcAuthUser != null && eit.EcAuthUser.Organization != null && eit.EcAuthUser.Organization.TenantName == _tenantService.TenantName && eit.EcAuthUser.Organization.DeletedAt == null);

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

            // Client.ClientId のグローバルユニーク制約
            modelBuilder.Entity<Client>()
                .HasIndex(c => c.ClientId)
                .IsUnique();

            modelBuilder.Entity<ExternalIdpToken>()
                .HasIndex(eit => new { eit.EcAuthSubject, eit.ExternalProvider })
                .IsUnique();

            modelBuilder.Entity<ExternalIdpToken>()
                .HasIndex(eit => eit.ExpiresAt);

            // B2BUser テナントフィルター
            modelBuilder.Entity<B2BUser>()
                .HasQueryFilter(u => u.Organization != null && u.Organization.TenantName == _tenantService.TenantName && u.Organization.DeletedAt == null);

            // B2BPasskeyCredential テナントフィルター（B2BUser経由）
            modelBuilder.Entity<B2BPasskeyCredential>()
                .HasQueryFilter(c => c.B2BUser != null && c.B2BUser.Organization != null && c.B2BUser.Organization.TenantName == _tenantService.TenantName && c.B2BUser.Organization.DeletedAt == null);

            // B2BUserIdentity テナントフィルター（B2BUser経由）
            modelBuilder.Entity<B2BUserIdentity>()
                .HasQueryFilter(i => i.B2BUser != null && i.B2BUser.Organization != null && i.B2BUser.Organization.TenantName == _tenantService.TenantName && i.B2BUser.Organization.DeletedAt == null);

            // WebAuthnChallenge テナントフィルター（Client経由）
            modelBuilder.Entity<WebAuthnChallenge>()
                .HasQueryFilter(wc => wc.Client != null && wc.Client.Organization != null && wc.Client.Organization.TenantName == _tenantService.TenantName && wc.Client.Organization.DeletedAt == null);

            // B2BUser 関連の設定
            // Subject（UUID）はグローバルに一意（RFC 9562）。EC-CUBEプラグインが生成するUUIDを
            // そのまま使用するため、Organization をまたいでも衝突しない設計。
            // Subject のみでユーザーを特定可能にしている。
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

            // b2b_user.external_id の一意性は意図的に外してある（EcAuthDocs#110）。
            // 識別子の一意性は b2b_user_identity の (issuer_key, external_id) が担保する。
            // ここを UNIQUE のまま残すと、同一 Organization に発行元の異なる Client が
            // ぶら下がる構成で「別発行元の同一 external_id」を作れず、本移行の目的
            //（EC-CUBE の member_id=1 と WordPress の user_id=1 の共存）が実 DB では成立しない。
            // 索引自体は移行期間中のフォールバック検索（GetByExternalIdAsync /
            // GetUnclaimedByExternalIdAsync）のために非一意で残す。
            modelBuilder.Entity<B2BUser>()
                .HasIndex(u => new { u.OrganizationId, u.ExternalId });

            // B2BUserIdentity 関連の設定（EcAuthDocs#110）
            //
            // 一意性は (issuer_key, external_id) の複合で担保する。external_id は発行元をまたぐと
            // 衝突しうる（EC-CUBE の member_id=1 と WordPress の user_id=1 は同一ハッシュ）が、
            // issuer_key が名前空間として分離する。issuer_key はグローバル一意な値
            // （client_id / IdP テナント ID）から構成されるため organization_id は不要。
            modelBuilder.Entity<B2BUser>()
                .HasMany(u => u.Identities)
                .WithOne(i => i.B2BUser)
                .HasForeignKey(i => i.B2BSubject)
                .HasPrincipalKey(u => u.Subject)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<B2BUserIdentity>()
                .HasIndex(i => new { i.IssuerKey, i.ExternalId })
                .IsUnique();

            // Client 解約時に該当 identity を一括で引くための索引（EcAuthDocs#110 / #111）。
            modelBuilder.Entity<B2BUserIdentity>()
                .HasIndex(i => i.ClientId);

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

            // RsaKeyPair: (OrganizationId, Kid) のユニーク制約
            modelBuilder.Entity<RsaKeyPair>()
                .HasIndex(k => new { k.OrganizationId, k.Kid })
                .IsUnique();

            // Account: 所属 Org のテナント (accounts / stg-accounts) のみ参照可能。
            // 所属先は申込受付テナントの Org であり顧客サイトの Org ではないため、通常は
            // 論理削除されない。それでも条件を揃えているのは、受付 Org が誤って削除された
            // 状態で Account だけ生き残る（＝どのテナントからも見えない Account が残る）
            // ズレを防ぐため。
            modelBuilder.Entity<Account>()
                .HasQueryFilter(a => a.Organization != null
                    && a.Organization.TenantName == _tenantService.TenantName
                    && a.Organization.DeletedAt == null);

            modelBuilder.Entity<Account>()
                .HasAlternateKey(a => a.Subject);

            modelBuilder.Entity<Account>()
                .HasOne(a => a.Organization)
                .WithMany()
                .HasForeignKey(a => a.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Account>()
                .HasIndex(a => new { a.OrganizationId, a.Email })
                .IsUnique();

            // 本番 Organization 数の上限。DB 側にも既定値を置くことで、この列を追加する
            // マイグレーションが既存の全アカウントに 10 を入れる（既定値なしだと 0 が入り、
            // 全既存アカウントがサイトを 1 件も追加できなくなる）。
            modelBuilder.Entity<Account>()
                .Property(a => a.MaxSites)
                .HasDefaultValue(Account.DefaultMaxSites);

            // AccountOrganization: テナント横断 (クエリフィルター対象外)
            modelBuilder.Entity<AccountOrganization>()
                .HasKey(ao => new { ao.AccountSubject, ao.OrganizationId });

            modelBuilder.Entity<AccountOrganization>()
                .HasOne(ao => ao.Account)
                .WithMany(a => a.ManagedOrganizations)
                .HasForeignKey(ao => ao.AccountSubject)
                .HasPrincipalKey(a => a.Subject)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AccountOrganization>()
                .HasOne(ao => ao.Organization)
                .WithMany()
                .HasForeignKey(ao => ao.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // MagicLoginToken: テナント横断のレート制限判定を行うため QueryFilter は設定しない
            modelBuilder.Entity<MagicLoginToken>()
                .HasOne(t => t.Account)
                .WithMany()
                .HasForeignKey(t => t.AccountSubject)
                .HasPrincipalKey(a => a.Subject)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MagicLoginToken>()
                .HasIndex(t => t.TokenHash)
                .IsUnique();

            modelBuilder.Entity<MagicLoginToken>()
                .HasIndex(t => t.ExpiresAt);

            // レート制限判定用の複合インデックス (Redis 不使用、DB ベース実装)
            modelBuilder.Entity<MagicLoginToken>()
                .HasIndex(t => new { t.RequestedEmailHash, t.CreatedAt });

            modelBuilder.Entity<MagicLoginToken>()
                .HasIndex(t => new { t.RequestedIp, t.CreatedAt });

            // PasskeyRegistrationToken: token_hash で検索するため一意インデックス。
            // テナント横断で検証するため QueryFilter は設定しない。
            modelBuilder.Entity<PasskeyRegistrationToken>()
                .HasIndex(t => t.TokenHash)
                .IsUnique();
            modelBuilder.Entity<PasskeyRegistrationToken>()
                .HasIndex(t => t.ExpiresAt);

            // SignupRequest: 申込段階では Organization 未作成のため、Organization 経由ではなく
            // TenantName カラムで直接テナントフィルターを適用する (Organization エンティティと同じ直接フィルター方式)。
            modelBuilder.Entity<SignupRequest>()
                .HasQueryFilter(sr => sr.TenantName == _tenantService.TenantName);

            // ConfirmTokenHash はグローバルにユニーク
            modelBuilder.Entity<SignupRequest>()
                .HasIndex(sr => sr.ConfirmTokenHash)
                .IsUnique()
                .HasDatabaseName("IX_signup_request_confirm_token_hash");

            // 未確認の申込をテナント単位で検索するための補助インデックス
            modelBuilder.Entity<SignupRequest>()
                .HasIndex(sr => new { sr.TenantName, sr.ConfirmedAt });

            base.OnModelCreating(modelBuilder);
        }
    }
}
