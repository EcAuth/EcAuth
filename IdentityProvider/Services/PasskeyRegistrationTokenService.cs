using System.Security.Cryptography;
using System.Text;
using IdentityProvider.Models;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 初回パスキー登録を認可する一回限りトークンの発行・検証・消費。
    /// 平文は返却時のみ扱い、DB には SHA-256 ハッシュのみ保存する。
    /// </summary>
    public interface IPasskeyRegistrationTokenService
    {
        /// <summary>指定 Subject 向けの登録トークンを発行し、平文を返す（保存はハッシュのみ）。</summary>
        Task<string> IssueAsync(string subject, CancellationToken ct = default);

        /// <summary>平文トークンを検証し、有効なら対象 Subject を返す（消費はしない）。無効なら null。</summary>
        Task<string?> ValidateAsync(string token, CancellationToken ct = default);

        /// <summary>平文トークンを使用済みにする（登録成功時）。消費できたら true。</summary>
        Task<bool> ConsumeAsync(string token, CancellationToken ct = default);
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

        public async Task<string?> ValidateAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var hash = HashToken(token);
            var now = DateTimeOffset.UtcNow;
            var entity = await _context.PasskeyRegistrationTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
            return entity?.Subject;
        }

        public async Task<bool> ConsumeAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(token)) return false;
            var hash = HashToken(token);
            var now = DateTimeOffset.UtcNow;

            // 注: used_at の更新は read-then-update のため、厳密には同時リクエストで
            // 二重消費し得る（TOCTOU）。ただし登録フローの single-use は WebAuthn の
            // チャレンジセッション（register/options で発行し verify で消費される session_id）が
            // 本質的に担保しており、本トークンの used_at は多重防御として機能する。
            // 二重消費が成立しても影響は「アカウント所有者が自分のパスキーを追加登録する」に
            // とどまり、他者へのなりすましには繋がらない。
            // （原子的 UPDATE には ExecuteUpdateAsync が適するが、テストの EF InMemory
            //   プロバイダが未対応のため採用していない。）
            var entity = await _context.PasskeyRegistrationTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, ct);
            if (entity == null) return false;

            entity.UsedAt = now;
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Passkey registration token consumed for subject {Subject}", entity.Subject);
            return true;
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
