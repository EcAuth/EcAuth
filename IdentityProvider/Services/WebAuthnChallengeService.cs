using System.Security.Cryptography;
using IdentityProvider.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentityProvider.Services
{
    /// <summary>
    /// WebAuthnチャレンジ管理サービスの実装
    /// </summary>
    public class WebAuthnChallengeService : IWebAuthnChallengeService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<WebAuthnChallengeService> _logger;

        /// <summary>
        /// チャレンジの有効期限（分）
        /// </summary>
        private const int ChallengeExpirationMinutes = 5;

        /// <summary>
        /// チャレンジのバイト数（32バイト = 256ビット）
        /// </summary>
        private const int ChallengeByteLength = 32;

        /// <summary>
        /// 有効なTypeの値
        /// </summary>
        private static readonly HashSet<string> ValidTypes = new() { "registration", "authentication" };

        /// <summary>
        /// 有効なUserTypeの値
        /// </summary>
        private static readonly HashSet<string> ValidUserTypes = new() { "b2b", "b2c" };

        public WebAuthnChallengeService(EcAuthDbContext context, ILogger<WebAuthnChallengeService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IWebAuthnChallengeService.ChallengeResult> GenerateChallengeAsync(
            IWebAuthnChallengeService.ChallengeRequest request)
        {
            // 入力検証
            ValidateRequest(request);

            // セッションID生成（UUID）
            var sessionId = Guid.NewGuid().ToString();

            // チャレンジ生成（32バイトランダム、Base64URL形式）
            var challengeBytes = RandomNumberGenerator.GetBytes(ChallengeByteLength);
            var challenge = Base64UrlTextEncoder.Encode(challengeBytes);

            // 有効期限計算（5分後）
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ChallengeExpirationMinutes);

            // DBに保存
            var webAuthnChallenge = new WebAuthnChallenge
            {
                Challenge = challenge,
                SessionId = sessionId,
                Type = request.Type,
                UserType = request.UserType,
                Subject = request.Subject,
                RpId = request.RpId,
                ClientId = request.ClientId,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _context.WebAuthnChallenges.Add(webAuthnChallenge);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "WebAuthnチャレンジを生成しました: SessionId={SessionId}, Type={Type}, UserType={UserType}",
                sessionId, request.Type, request.UserType);

            return new IWebAuthnChallengeService.ChallengeResult
            {
                SessionId = sessionId,
                Challenge = challenge,
                ExpiresAt = expiresAt
            };
        }

        /// <inheritdoc />
        public async Task<WebAuthnChallenge?> GetChallengeBySessionIdAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var challenge = await _context.WebAuthnChallenges
                .Include(c => c.Client)
                    .ThenInclude(c => c!.Organization)
                .FirstOrDefaultAsync(c => c.SessionId == sessionId);

            if (challenge == null)
            {
                _logger.LogDebug("チャレンジが見つかりません: SessionId={SessionId}", sessionId);
                return null;
            }

            // 期限切れチェック
            if (challenge.IsExpired)
            {
                _logger.LogDebug("チャレンジが期限切れです: SessionId={SessionId}", sessionId);
                return null;
            }

            return challenge;
        }

        /// <inheritdoc />
        public async Task<bool> ConsumeChallengeAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            // 期限切れ含めて全て削除対象
            var challenge = await _context.WebAuthnChallenges
                .FirstOrDefaultAsync(c => c.SessionId == sessionId);

            if (challenge == null)
            {
                _logger.LogDebug("消費するチャレンジが見つかりません: SessionId={SessionId}", sessionId);
                return false;
            }

            _context.WebAuthnChallenges.Remove(challenge);
            await _context.SaveChangesAsync();

            _logger.LogInformation("チャレンジを消費しました: SessionId={SessionId}", sessionId);
            return true;
        }

        /// <inheritdoc />
        public async Task<int> CleanupExpiredChallengesAsync()
        {
            var now = DateTimeOffset.UtcNow;

            var expiredChallenges = await _context.WebAuthnChallenges
                .Where(c => c.ExpiresAt <= now)
                .ToListAsync();

            if (expiredChallenges.Count == 0)
            {
                return 0;
            }

            _context.WebAuthnChallenges.RemoveRange(expiredChallenges);
            await _context.SaveChangesAsync();

            _logger.LogInformation("期限切れチャレンジをクリーンアップしました: 削除件数={Count}", expiredChallenges.Count);
            return expiredChallenges.Count;
        }

        /// <summary>
        /// リクエストの検証
        /// </summary>
        private void ValidateRequest(IWebAuthnChallengeService.ChallengeRequest request)
        {
            // Type検証
            if (!ValidTypes.Contains(request.Type))
            {
                throw new ArgumentException(
                    $"Type は 'registration' または 'authentication' である必要があります。指定された値: '{request.Type}'",
                    nameof(request));
            }

            // UserType検証
            if (!ValidUserTypes.Contains(request.UserType))
            {
                throw new ArgumentException(
                    $"UserType は 'b2b' または 'b2c' である必要があります。指定された値: '{request.UserType}'",
                    nameof(request));
            }

            // ClientId検証
            if (request.ClientId <= 0)
            {
                throw new ArgumentException("ClientId は正の整数である必要があります。", nameof(request));
            }

            // Subject検証（条件付き）
            // B2B登録ではSubject必須（パスキーを紐付けるユーザーが必要）
            // B2B認証ではSubject省略可（Discoverable Credentials対応）
            // B2C認証ではSubject必須、B2C登録ではnull許容
            if (request.UserType == "b2b" && request.Type == "registration" && string.IsNullOrWhiteSpace(request.Subject))
            {
                throw new ArgumentException("B2B登録の場合、Subject は必須です。", nameof(request));
            }

            if (request.UserType == "b2c" && request.Type == "authentication" && string.IsNullOrWhiteSpace(request.Subject))
            {
                throw new ArgumentException("B2C認証の場合、Subject は必須です。", nameof(request));
            }
        }
    }
}
