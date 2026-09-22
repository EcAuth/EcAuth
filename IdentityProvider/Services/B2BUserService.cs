using IdentityProvider.Exceptions;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2Bユーザー管理サービスの実装
    /// </summary>
    public class B2BUserService : IB2BUserService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<B2BUserService> _logger;

        public B2BUserService(EcAuthDbContext context, ILogger<B2BUserService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IB2BUserService.CreateUserResult> CreateAsync(IB2BUserService.CreateUserRequest request)
        {
            // 入力検証
            ValidateCreateRequest(request);

            // Subject生成（UUID）- 指定された場合はバリデーション後にそのまま使用
            string subject;
            if (!string.IsNullOrWhiteSpace(request.Subject))
            {
                if (!Guid.TryParse(request.Subject, out var parsedGuid))
                    throw new ArgumentException("Subject は有効な UUID 形式である必要があります。", nameof(request));
                subject = parsedGuid.ToString();
            }
            else
            {
                subject = Guid.NewGuid().ToString();
            }
            var now = DateTimeOffset.UtcNow;

            var user = new B2BUser
            {
                Subject = subject,
                UserType = request.UserType,
                OrganizationId = request.OrganizationId,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.B2BUsers.Add(user);

            // 識別子の置き場は b2b_user_identity のみ（EcAuthDocs#110）。
            // external_id は個人情報を含み得るため、正規化 + SHA-256 ハッシュ化して保持する。
            _context.B2BUserIdentities.Add(new B2BUserIdentity
            {
                B2BSubject = subject,
                IssuerKey = request.IssuerKey,
                ExternalId = ExternalIdHasher.Hash(request.ExternalId),
                ClientId = request.ClientId,
                CreatedAt = now
            });

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "B2Bユーザーを作成しました: Subject={Subject}, UserType={UserType}, OrganizationId={OrganizationId}",
                subject, request.UserType, request.OrganizationId);

            return new IB2BUserService.CreateUserResult
            {
                User = user
            };
        }

        /// <inheritdoc />
        public async Task<B2BUser?> GetBySubjectAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return null;
            }

            var user = await _context.B2BUsers
                .Include(u => u.Organization)
                .Include(u => u.PasskeyCredentials)
                .FirstOrDefaultAsync(u => u.Subject == subject);

            if (user == null)
            {
                _logger.LogDebug("B2Bユーザーが見つかりません: Subject={Subject}", subject);
            }

            return user;
        }

        /// <inheritdoc />
        public async Task<B2BUser?> GetByIdentityAsync(string issuerKey, string externalId)
        {
            if (string.IsNullOrWhiteSpace(issuerKey) || string.IsNullOrWhiteSpace(externalId))
            {
                return null;
            }

            // external_id はハッシュ化して保持しているため、検索キーも同じく正規化 + ハッシュ化する。
            var externalIdHash = ExternalIdHasher.Hash(externalId);

            var subject = await _context.B2BUserIdentities
                .Where(i => i.IssuerKey == issuerKey && i.ExternalId == externalIdHash)
                .Select(i => i.B2BSubject)
                .FirstOrDefaultAsync();

            if (subject == null)
            {
                // 平文 external_id は PII を含み得るためログにはハッシュ値のみ残す。
                _logger.LogDebug(
                    "B2BUserIdentity が見つかりません: IssuerKey={IssuerKey}, ExternalIdHash={ExternalIdHash}",
                    issuerKey, externalIdHash);
                return null;
            }

            return await GetBySubjectAsync(subject);
        }

        /// <inheritdoc />
        public Task EnsureIdentityAsync(string subject, string issuerKey, string externalId, string? clientId)
        {
            if (string.IsNullOrWhiteSpace(externalId))
            {
                throw new ArgumentException("ExternalId は必須です。", nameof(externalId));
            }

            return EnsureIdentityByHashAsync(
                subject, issuerKey, ExternalIdHasher.Hash(externalId), clientId);
        }

        /// <summary>
        /// <see cref="EnsureIdentityAsync"/> の本体（ハッシュ値受け取り）。
        /// </summary>
        private async Task EnsureIdentityByHashAsync(
            string subject, string issuerKey, string externalIdHash, string? clientId)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new ArgumentException("Subject は必須です。", nameof(subject));
            }

            if (string.IsNullOrWhiteSpace(issuerKey))
            {
                throw new ArgumentException("IssuerKey は必須です。", nameof(issuerKey));
            }

            if (string.IsNullOrWhiteSpace(externalIdHash))
            {
                throw new ArgumentException("ExternalIdHash は必須です。", nameof(externalIdHash));
            }

            // 同一 (issuer_key, external_id) が既にあれば何もしない。EcAuthDocs#110 の決定により
            // 旧識別子は削除せず共存させるため、ここは insert-if-missing であって update ではない。
            var existingOwner = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .Where(i => i.IssuerKey == issuerKey && i.ExternalId == externalIdHash)
                .Select(i => i.B2BSubject)
                .FirstOrDefaultAsync();

            if (existingOwner != null)
            {
                if (!string.Equals(existingOwner, subject, StringComparison.Ordinal))
                {
                    // 同一発行元の同一 external_id を別人が保有している。黙って無視すると
                    // 「登録したはずの識別子で別人に解決される」状態を作るため 409 相当で弾く。
                    throw new ExternalIdConflictException(
                        $"ExternalId (hash '{externalIdHash}') is already used by another user under issuer '{issuerKey}'.");
                }

                return;
            }

            var identity = new B2BUserIdentity
            {
                B2BSubject = subject,
                IssuerKey = issuerKey,
                ExternalId = externalIdHash,
                ClientId = clientId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _context.B2BUserIdentities.Add(identity);

            try
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation(
                    "B2BUserIdentity を作成しました: Subject={Subject}, IssuerKey={IssuerKey}",
                    subject, issuerKey);
            }
            catch (DbUpdateException ex)
            {
                // 失敗した行だけを追跡から外す。ChangeTracker.Clear() は呼び出し元が保持している
                // 未保存エンティティまで破棄してしまうため使わない。
                _context.Entry(identity).State = EntityState.Detached;

                // UNIQUE 制約違反（並行リクエストが先に同じ (issuer_key, external_id) を作った）かを
                // 実状態で再確認する。SQL エラーコード判定ではなく所有者の有無で判定することで
                // DB プロバイダー非依存に race を検出できる。それ以外の障害（タイムアウト・接続断・
                // 別制約違反）は握り潰さず再スローする。
                //
                // 呼び出し元がトランザクションを張っている場合、SaveChangesAsync の失敗内容に
                // よってはそのトランザクションが既に使用不能になっている（デッドロック被害など）。
                // その状態でこの再確認クエリを投げると "transaction has completed" 等で失敗し、
                // 元の DbUpdateException を覆い隠すため、再確認の失敗は握って元例外を再スローする。
                // トランザクションの破棄は所有者である呼び出し元の責務なのでここでは行わない。
                string? owner = null;
                try
                {
                    owner = await _context.B2BUserIdentities
                        .IgnoreQueryFilters()
                        .Where(i => i.IssuerKey == issuerKey && i.ExternalId == externalIdHash)
                        .Select(i => i.B2BSubject)
                        .FirstOrDefaultAsync();
                }
                catch (Exception probeEx)
                {
                    // race かどうかを判定できないため、下の throw; で元の DbUpdateException を伝える。
                    _logger.LogWarning(
                        probeEx,
                        "B2BUserIdentity の重複再確認に失敗しました: Subject={Subject}, IssuerKey={IssuerKey}",
                        subject, issuerKey);
                }

                if (owner == null)
                {
                    throw;
                }

                if (!string.Equals(owner, subject, StringComparison.Ordinal))
                {
                    // 同一発行元の同一 external_id を別人が保有している。これは race ではなく
                    // 本物の衝突なので 409 相当として扱う（例外メッセージには平文を含めない）。
                    throw new ExternalIdConflictException(
                        $"ExternalId (hash '{externalIdHash}') is already used by another user under issuer '{issuerKey}'.",
                        ex);
                }

                _logger.LogInformation(
                    "B2BUserIdentity は並行リクエストにより作成済みでした: Subject={Subject}, IssuerKey={IssuerKey}",
                    owner, issuerKey);
            }
        }

        /// <inheritdoc />
        public async Task<B2BUser?> UpdateAsync(IB2BUserService.UpdateUserRequest request)
        {
            // 入力検証
            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                throw new ArgumentException("Subject は必須です。", nameof(request));
            }

            var user = await _context.B2BUsers
                .FirstOrDefaultAsync(u => u.Subject == request.Subject);

            if (user == null)
            {
                _logger.LogDebug("更新対象のB2Bユーザーが見つかりません: Subject={Subject}", request.Subject);
                return null;
            }

            // 部分更新（nullでないフィールドのみ更新）
            if (request.UserType != null)
            {
                user.UserType = request.UserType;
            }

            user.UpdatedAt = DateTimeOffset.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("B2Bユーザーを更新しました: Subject={Subject}", request.Subject);

            return user;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return false;
            }

            var user = await _context.B2BUsers
                .FirstOrDefaultAsync(u => u.Subject == subject);

            if (user == null)
            {
                _logger.LogDebug("削除対象のB2Bユーザーが見つかりません: Subject={Subject}", subject);
                return false;
            }

            _context.B2BUsers.Remove(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("B2Bユーザーを削除しました: Subject={Subject}", subject);
            return true;
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return false;
            }

            return await _context.B2BUsers.AnyAsync(u => u.Subject == subject);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<string, int>> CountByClientsAsync(IReadOnlyCollection<string> clientIds)
        {
            if (clientIds.Count == 0)
            {
                return new Dictionary<string, int>();
            }

            // 呼び出し元は accounts テナント（マイページ）や ConsoleApp なので顧客 Organization の行を横断で読む。
            var counts = await _context.B2BUserIdentities
                .IgnoreQueryFilters()
                .Where(i => i.ClientId != null && clientIds.Contains(i.ClientId))
                .GroupBy(i => i.ClientId!)
                .Select(g => new { ClientId = g.Key, Count = g.Select(i => i.B2BSubject).Distinct().Count() })
                .ToListAsync();

            return counts.ToDictionary(c => c.ClientId, c => c.Count);
        }

        /// <summary>
        /// 作成リクエストの検証
        /// </summary>
        private static void ValidateCreateRequest(IB2BUserService.CreateUserRequest request)
        {
            if (request.OrganizationId <= 0)
            {
                throw new ArgumentException("OrganizationId は正の整数である必要があります。", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.UserType))
            {
                throw new ArgumentException("UserType は必須です。", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ExternalId))
            {
                throw new ArgumentException("ExternalId は必須です。", nameof(request));
            }

            // external_id は発行元をまたぐと衝突しうるため、名前空間なしでは登録させない（EcAuthDocs#110）。
            if (string.IsNullOrWhiteSpace(request.IssuerKey))
            {
                throw new ArgumentException("IssuerKey は必須です。", nameof(request));
            }
        }
    }
}
