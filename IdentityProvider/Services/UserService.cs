using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IdpUtilities;
using IdentityProvider.Data;

namespace IdentityProvider.Services
{
    public class UserService : IUserService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<UserService> _logger;

        public UserService(EcAuthDbContext context, ILogger<UserService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<EcAuthUser> ResolveFederatedUserAsync(IUserService.FederatedLoginRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Client);
            ArgumentNullException.ThrowIfNull(request.Provider);
            if (string.IsNullOrWhiteSpace(request.ExternalSubject))
                throw new ArgumentException("ExternalSubject cannot be null or empty.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Client.ClientId))
                throw new ArgumentException("Client.ClientId cannot be null or empty.", nameof(request));
            if (request.Client.OrganizationId is not > 0)
                throw new ArgumentException("Client.OrganizationId must be positive.", nameof(request));
            if (request.Provider.ClientId != request.Client.Id)
                throw new ArgumentException("Provider does not belong to Client.", nameof(request));

            var login = new ResolvedLogin(
                ClientId: request.Client.ClientId,
                OrganizationId: request.Client.OrganizationId.Value,
                ProviderName: request.Provider.Name,
                ExternalSubject: request.ExternalSubject,
                IssuerKey: B2CIssuerKey.ForIdp(request.Provider.Id),
                ExternalId: ExternalIdHasher.HashCaseSensitive(request.ExternalSubject),
                EmailHash: HashEmailOrEmpty(request.Email, request.Provider.Name),
                EmailVerified: request.EmailVerified);

            try
            {
                return await ResolveOnceAsync(login);
            }
            catch (DbUpdateException ex) when (DatabaseResilience.IsUniqueConstraintViolation(ex))
            {
                // 同じ sub の初回ログインが並行すると、後から来た側は identity の一意制約に当たる。
                // 先に作られた identity を引き直せば同じユーザーに解決できる（1 回だけ）。
                _logger.LogInformation(ex, "Concurrent federated login detected, retrying resolution. IssuerKey: {IssuerKey}", login.IssuerKey);
                _context.ChangeTracker.Clear();
                return await ResolveOnceAsync(login);
            }
        }

        private sealed record ResolvedLogin(
            string ClientId,
            int OrganizationId,
            string ProviderName,
            string ExternalSubject,
            string IssuerKey,
            string ExternalId,
            string EmailHash,
            bool EmailVerified);

        /// <remarks>
        /// テナントのクエリフィルターは使わない（<c>IgnoreQueryFilters</c>）。/v1/auth/callback は
        /// 外部 IdP から固定のホストへ戻ってくるため、リクエストのテナントがログイン先の
        /// Organization と一致するとは限らない。代わりに、issuer_key（OpenIdProvider の行 = Client）で
        /// 引き、見つけたユーザーの Client / Organization を明示的に照合する。
        /// </remarks>
        private async Task<EcAuthUser> ResolveOnceAsync(ResolvedLogin login, bool claimLost = false)
        {
            var now = DateTimeOffset.UtcNow;

            // 1. identity が見つかる → そのユーザー（通常のログイン）
            var identity = await _context.EcAuthUserIdentities
                .IgnoreQueryFilters()
                .Include(i => i.EcAuthUser)
                .FirstOrDefaultAsync(i => i.IssuerKey == login.IssuerKey && i.ExternalId == login.ExternalId);
            if (identity?.EcAuthUser != null)
            {
                var user = identity.EcAuthUser;
                EnsureSamePool(user, login);
                user.ClientId ??= login.ClientId;
                ApplyEmail(user, login, now);
                await _context.SaveChangesAsync();
                return user;
            }

            // 2. identity が無く、旧 ExternalIdpMapping で見つかる → identity を足して返す（遅延移行）。
            // 旧構造は provider 名で区切っており Client 間で衝突しうるため、同じ Organization かつ
            // 同じ Client（backfill で Client を決められなかった null を含む）のユーザーに限る。
            var mappedUser = await _context.ExternalIdpMappings
                .IgnoreQueryFilters()
                .Where(m => m.ExternalProvider == login.ProviderName
                    && m.ExternalSubject == login.ExternalSubject
                    && m.EcAuthUser != null
                    && m.EcAuthUser.OrganizationId == login.OrganizationId
                    && (m.EcAuthUser.ClientId == null || m.EcAuthUser.ClientId == login.ClientId))
                .Select(m => m.EcAuthUser!)
                .FirstOrDefaultAsync();
            if (mappedUser != null)
            {
                _logger.LogInformation("Migrating ExternalIdpMapping to identity. EcAuthSubject: {Subject}, IssuerKey: {IssuerKey}",
                    mappedUser.Subject, login.IssuerKey);
                if (mappedUser.ClientId == null)
                {
                    // backfill で Client を決められなかった旧ユーザーのプールを確定する。同じ Organization の
                    // 別 Client から同時にログインが来ると両方が null を読むので、null のときだけ書く条件付き
                    // UPDATE で 1 つの Client だけに確定させる。負けた側は、そのユーザーを自分のプールの
                    // ユーザーとして扱わず、解決をやり直す（mapping の候補から外れ、統合か新規作成になる）。
                    if (await TryClaimClientAsync(mappedUser.Id, login.ClientId) == 0 && !claimLost)
                    {
                        _logger.LogInformation("Legacy user was claimed by another client concurrently. EcAuthSubject: {Subject}, ClientId: {ClientId}",
                            mappedUser.Subject, login.ClientId);
                        _context.ChangeTracker.Clear();
                        return await ResolveOnceAsync(login, claimLost: true);
                    }
                    mappedUser.ClientId = login.ClientId;
                }
                AddIdentity(mappedUser, login, now);
                ApplyEmail(mappedUser, login, now);
                await _context.SaveChangesAsync();
                return mappedUser;
            }

            // 3. 確認済みメールアドレスが同じ Client 内の確認済みユーザー 1 人と一致 → 統合
            if (login.EmailVerified && login.EmailHash.Length > 0)
            {
                var candidates = await _context.EcAuthUsers
                    .IgnoreQueryFilters()
                    .Where(u => u.ClientId == login.ClientId
                        && u.OrganizationId == login.OrganizationId
                        && u.EmailHash == login.EmailHash
                        && u.EmailVerifiedAt != null)
                    .Take(2)
                    .ToListAsync();
                if (candidates.Count == 1)
                {
                    var target = candidates[0];
                    _logger.LogInformation("Linking identity to existing user by verified email. EcAuthSubject: {Subject}, IssuerKey: {IssuerKey}",
                        target.Subject, login.IssuerKey);
                    AddIdentity(target, login, now);
                    await AddLegacyMappingIfFreeAsync(target, login, now);
                    await _context.SaveChangesAsync();
                    return target;
                }
                if (candidates.Count > 1)
                {
                    // 候補が複数あるときは統合しない（どれに足すか決められない）
                    _logger.LogWarning("Multiple verified users share the email hash; creating a new user. ClientId: {ClientId}", login.ClientId);
                }
            }

            // 4. それ以外 → 新しいユーザーと identity を作る（JIT プロビジョニング）
            var newUser = new EcAuthUser
            {
                Subject = Guid.NewGuid().ToString(),
                EmailHash = login.EmailHash,
                EmailVerifiedAt = login.EmailVerified && login.EmailHash.Length > 0 ? now : null,
                OrganizationId = login.OrganizationId,
                ClientId = login.ClientId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.EcAuthUsers.Add(newUser);
            AddIdentity(newUser, login, now);
            await AddLegacyMappingIfFreeAsync(newUser, login, now);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created B2C user by JIT provisioning. EcAuthSubject: {Subject}, IssuerKey: {IssuerKey}",
                newUser.Subject, login.IssuerKey);
            return newUser;
        }

        /// <summary>
        /// client_id がまだ null のときだけ、ユーザーのプールを <paramref name="clientId"/> に確定する。
        /// 戻り値は更新した行数（0 なら別の要求が先に確定した）。
        /// InMemory プロバイダーは <c>ExecuteUpdate</c> 非対応のため、ユニットテストでは override する
        /// （<see cref="AuthorizationCodeService"/> の <c>TryMarkAsUsedAsync</c> と同じ流儀）。
        /// </summary>
        protected virtual Task<int> TryClaimClientAsync(int userId, string clientId)
        {
            return _context.EcAuthUsers
                .IgnoreQueryFilters()
                .Where(u => u.Id == userId && u.ClientId == null)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.ClientId, clientId));
        }

        private void EnsureSamePool(EcAuthUser user, ResolvedLogin login)
        {
            if (user.OrganizationId != login.OrganizationId
                || (user.ClientId != null && user.ClientId != login.ClientId))
            {
                _logger.LogError("Identity resolves to a user in another pool. EcAuthSubject: {Subject}, IssuerKey: {IssuerKey}, ClientId: {ClientId}",
                    user.Subject, login.IssuerKey, login.ClientId);
                throw new UserPoolMismatchException("identity が別のユーザープールのユーザーを指しています。");
            }
        }

        private void AddIdentity(EcAuthUser user, ResolvedLogin login, DateTimeOffset now)
        {
            _context.EcAuthUserIdentities.Add(new EcAuthUserIdentity
            {
                EcAuthSubject = user.Subject,
                IssuerKey = login.IssuerKey,
                ExternalId = login.ExternalId,
                ClientId = login.ClientId,
                CreatedAt = now,
                EcAuthUser = user
            });
        }

        /// <summary>
        /// 旧 ExternalIdpMapping への二重書き（expand 期間だけ。ロールアウト中の旧インスタンスが
        /// 同じユーザーに解決できるようにする）。旧構造の一意制約 (external_provider, external_subject) は
        /// Organization をまたいで効くので、既に行があるときは書かない。書くとこの変更で解消した
        /// 制約違反を二重書きが作り直してしまう。
        /// </summary>
        private async Task AddLegacyMappingIfFreeAsync(EcAuthUser user, ResolvedLogin login, DateTimeOffset now)
        {
            var taken = await _context.ExternalIdpMappings
                .IgnoreQueryFilters()
                .AnyAsync(m => m.ExternalProvider == login.ProviderName && m.ExternalSubject == login.ExternalSubject);
            if (taken)
            {
                return;
            }
            _context.ExternalIdpMappings.Add(new ExternalIdpMapping
            {
                EcAuthSubject = user.Subject,
                ExternalProvider = login.ProviderName,
                ExternalSubject = login.ExternalSubject,
                CreatedAt = now,
                EcAuthUser = user  // Navigation propertyを明示的に設定（EF Core 9マルチテナントフィルタ対応）
            });
        }

        /// <summary>
        /// ログインで得たメールアドレスをユーザーに反映する。
        /// <list type="bullet">
        /// <item>メールアドレスが無いログイン（LINE など）では触らない。別の識別子で得た値を消さないため</item>
        /// <item>変わっていたら更新し、確認状態は今回の値にする</item>
        /// <item>同じなら、今回確認済みのときだけ確認時刻を埋める（未確認の表明で降格はしない）</item>
        /// </list>
        /// </summary>
        private static void ApplyEmail(EcAuthUser user, ResolvedLogin login, DateTimeOffset now)
        {
            if (login.EmailHash.Length == 0)
            {
                return;
            }
            if (user.EmailHash != login.EmailHash)
            {
                user.EmailHash = login.EmailHash;
                user.EmailVerifiedAt = login.EmailVerified ? now : null;
                user.UpdatedAt = now;
            }
            else if (login.EmailVerified && user.EmailVerifiedAt == null)
            {
                user.EmailVerifiedAt = now;
                user.UpdatedAt = now;
            }
        }

        private string HashEmailOrEmpty(string? email, string providerName)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return string.Empty;
            }
            try
            {
                return EmailHashUtil.HashEmail(email);
            }
            catch (Exception ex)
            {
                // メールハッシュ化に失敗した場合は空文字列のまま続行（従来どおり）
                _logger.LogWarning(ex, "Failed to hash email for user. Provider: {Provider}", providerName);
                return string.Empty;
            }
        }

        public async Task<EcAuthUser?> GetUserBySubjectAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
                return null;

            return await _context.EcAuthUsers
                .Include(u => u.Organization)
                .Include(u => u.ExternalIdpMappings)
                .FirstOrDefaultAsync(u => u.Subject == subject);
        }

        public async Task<List<EcAuthUser>> GetUsersByEmailHashAsync(string emailHash, int? organizationId = null)
        {
            if (string.IsNullOrWhiteSpace(emailHash))
            {
                _logger.LogDebug("Empty email hash provided to GetUsersByEmailHashAsync");
                return new List<EcAuthUser>();
            }

            try
            {
                var query = _context.EcAuthUsers
                    .Include(u => u.Organization)
                    .Include(u => u.ExternalIdpMappings)
                    .Where(u => u.EmailHash == emailHash);

                if (organizationId.HasValue)
                {
                    query = query.Where(u => u.OrganizationId == organizationId.Value);
                    _logger.LogDebug("Searching users by email hash for specific organization. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                        emailHash, organizationId.Value);
                }
                else
                {
                    _logger.LogDebug("Searching users by email hash across all organizations. EmailHash: {EmailHash}",
                        emailHash);
                }

                var users = await query.ToListAsync();

                _logger.LogInformation("Found {UserCount} users with email hash. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                    users.Count, emailHash, organizationId);

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve users by email hash. EmailHash: {EmailHash}, OrganizationId: {OrganizationId}",
                    emailHash, organizationId);
                throw;
            }
        }
    }
}