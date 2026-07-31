using System.Security.Cryptography;
using System.Text;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 登録トークンの検証結果。対象 Subject と、束縛済みの WebAuthn セッション ID を返す。
    /// </summary>
    /// <param name="Subject">登録対象アカウントの統一 Subject。</param>
    /// <param name="SessionId">
    /// register/options で束縛された session_id。未束縛（options 未実行）なら null。
    /// </param>
    public record PasskeyRegistrationTokenInfo(string Subject, string? SessionId);

    /// <summary>
    /// 初回パスキー登録を認可する一回限りトークンの発行・検証・消費。
    /// 平文は返却時のみ扱い、DB には SHA-256 ハッシュのみ保存する。
    /// </summary>
    public interface IPasskeyRegistrationTokenService
    {
        /// <summary>指定 Subject 向けの登録トークンを発行し、平文を返す（保存はハッシュのみ）。</summary>
        Task<string> IssueAsync(string subject, CancellationToken ct = default);

        /// <summary>平文トークンを検証し、有効なら対象 Subject と束縛セッションを返す（消費はしない）。無効なら null。</summary>
        Task<PasskeyRegistrationTokenInfo?> ValidateAsync(string token, CancellationToken ct = default);

        /// <summary>
        /// register/options が発行した session_id をトークンへ束縛する（既存の束縛は上書きする）。
        /// これにより「同時に verify できるセッションは常に 1 つ」に制限する。
        /// </summary>
        Task<bool> BindSessionAsync(string token, string sessionId, CancellationToken ct = default);

        /// <summary>
        /// 平文トークンを使用済みにする。<paramref name="sessionId"/> が束縛済みの値と一致する場合のみ消費する。
        /// 消費できたら true（並行呼び出しでは 1 つだけが true を返す）。
        /// </summary>
        Task<bool> ConsumeAsync(string token, string sessionId, CancellationToken ct = default);
    }

    public class PasskeyRegistrationTokenService : IPasskeyRegistrationTokenService
    {
        private const int TokenBytes = 32;
        private const int ExpirationMinutes = 30;

        private readonly EcAuthDbContext _context;
        private readonly ILogger<PasskeyRegistrationTokenService> _logger;

        public PasskeyRegistrationTokenService(EcAuthDbContext context, ILogger<PasskeyRegistrationTokenService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<string> IssueAsync(string subject, CancellationToken ct = default)
        {
            var token = GenerateToken();
            _context.PasskeyRegistrationTokens.Add(new PasskeyRegistrationToken
            {
                Subject = subject,
                TokenHash = HashToken(token),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(ExpirationMinutes),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Passkey registration token issued for subject {Subject}", subject);
            return token;
        }

        public async Task<PasskeyRegistrationTokenInfo?> ValidateAsync(string token, CancellationToken ct = default)
        {
            var entity = await FindUsableAsync(token, ct);
            return entity == null ? null : new PasskeyRegistrationTokenInfo(entity.Subject, entity.SessionId);
        }

        public async Task<bool> BindSessionAsync(string token, string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            var entity = await FindUsableAsync(token, ct);
            if (entity == null) return false;

            // 束縛先は毎回上書きする。正規ユーザーがやり直し（options 再実行）できる一方で、
            // 過去に発行された session_id は verify できなくなる。
            entity.SessionId = sessionId;
            await _context.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> ConsumeAsync(string token, string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            var entity = await FindUsableAsync(token, ct);
            if (entity == null) return false;

            // 提示された session_id が束縛済みのものと一致しない場合は消費しない。
            // （同一トークンから派生した「古い」セッションでの登録を遮断する）
            if (!string.Equals(entity.SessionId, sessionId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Passkey registration token presented with a session that is not bound to it. Subject={Subject}",
                    entity.Subject);
                return false;
            }

            entity.UsedAt = DateTimeOffset.UtcNow;
            try
            {
                // UsedAt は [ConcurrencyCheck] のため、UPDATE は「まだ未使用」の行にしか当たらない。
                // 並行消費が起きた場合、敗者は 0 行更新となり DbUpdateConcurrencyException になる。
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning(
                    "Concurrent consumption detected for passkey registration token. Subject={Subject}",
                    entity.Subject);
                // 追跡中のエンティティを破棄し、この呼び出しは失敗として扱う。
                _context.Entry(entity).State = EntityState.Detached;
                return false;
            }

            _logger.LogInformation("Passkey registration token consumed for subject {Subject}", entity.Subject);
            return true;
        }

        /// <summary>未使用かつ未期限切れのトークン行を取得する。</summary>
        private async Task<PasskeyRegistrationToken?> FindUsableAsync(string token, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var hash = HashToken(token);
            var now = DateTimeOffset.UtcNow;
            return await _context.PasskeyRegistrationTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
        }

        private static string GenerateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string HashToken(string token)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        }
    }
}
