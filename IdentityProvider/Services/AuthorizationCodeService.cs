using System.Security.Cryptography;
using IdentityProvider.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    public class AuthorizationCodeService : IAuthorizationCodeService
    {
        private readonly EcAuthDbContext _context;
        private readonly ILogger<AuthorizationCodeService> _logger;

        public AuthorizationCodeService(EcAuthDbContext context, ILogger<AuthorizationCodeService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// セキュアなコード生成（32バイトのランダム値をBase64URL形式で）
        /// </summary>
        private string GenerateSecureCode()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base64UrlTextEncoder.Encode(bytes);
        }

        /// <summary>
        /// コードが既に存在するかチェック
        /// </summary>
        private async Task<bool> CodeExistsAsync(string code)
        {
            return await _context.AuthorizationCodes
                .AnyAsync(ac => ac.Code == code);
        }

        /// <summary>
        /// リクエストパラメータの検証
        /// </summary>
        private void ValidateRequest(IAuthorizationCodeService.AuthorizationCodeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Subject))
                throw new ArgumentException("Subject は必須です", nameof(request.Subject));

            if (request.ClientId <= 0)
                throw new ArgumentException("ClientId は正の値である必要があります", nameof(request.ClientId));

            if (string.IsNullOrWhiteSpace(request.RedirectUri))
                throw new ArgumentException("RedirectUri は必須です", nameof(request.RedirectUri));

            if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out _))
                throw new ArgumentException("RedirectUri は有効なURLである必要があります", nameof(request.RedirectUri));

            if (request.ExpirationMinutes <= 0 || request.ExpirationMinutes > 30)
                throw new ArgumentException("ExpirationMinutes は1分から30分の範囲で指定してください", nameof(request.ExpirationMinutes));
        }


        public async Task<AuthorizationCode> GenerateAuthorizationCodeAsync(
            IAuthorizationCodeService.AuthorizationCodeRequest request)
        {
            // パラメータ検証
            ValidateRequest(request);

            // ユニークなコードを生成（衝突チェック付き）
            string code;
            int attempts = 0;
            do
            {
                code = GenerateSecureCode();
                attempts++;
                if (attempts > 5)
                {
                    _logger.LogError("認可コード生成で最大試行回数に達しました");
                    throw new InvalidOperationException("認可コード生成に失敗しました");
                }
            } while (await CodeExistsAsync(code));

            // SubjectType を決定（IsB2B フラグから推測）
            var subjectType = request.IsB2B ? Models.SubjectType.B2B : Models.SubjectType.B2C;

            // 認可コードエンティティ作成
            var authorizationCode = new AuthorizationCode
            {
                Code = code,
                // 統一Subject と SubjectType を設定
                Subject = request.Subject,
                SubjectType = subjectType,
                ClientId = request.ClientId,
                RedirectUri = request.RedirectUri,
                Scope = request.Scope,
                State = request.State,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(request.ExpirationMinutes),
                IsUsed = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // データベースに保存
            _context.AuthorizationCodes.Add(authorizationCode);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "認可コード生成: Subject={Subject}, SubjectType={SubjectType}, ClientId={ClientId}, ExpiresAt={ExpiresAt}",
                request.Subject, subjectType, request.ClientId, authorizationCode.ExpiresAt);

            return authorizationCode;
        }

        public async Task<AuthorizationCode?> GetAuthorizationCodeAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            var authorizationCode = await _context.AuthorizationCodes
                .Include(ac => ac.Client)
                    .ThenInclude(c => c!.Organization)
                .FirstOrDefaultAsync(ac => ac.Code == code);

            if (authorizationCode == null)
            {
                _logger.LogWarning("認可コードが見つかりません: Code={Code}", code);
                return null;
            }

            // 期限切れチェック
            if (authorizationCode.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _logger.LogWarning(
                    "期限切れの認可コードが要求されました: Code={Code}, ExpiresAt={ExpiresAt}",
                    code, authorizationCode.ExpiresAt);
                return null;
            }

            // 既に使用済みかチェック
            if (authorizationCode.IsUsed)
            {
                _logger.LogWarning(
                    "使用済みの認可コードが要求されました: Code={Code}, UsedAt={UsedAt}",
                    code, authorizationCode.UsedAt);
                return null;
            }

            return authorizationCode;
        }

        public async Task<bool> MarkAsUsedAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            var authorizationCode = await _context.AuthorizationCodes
                .Include(ac => ac.Client)
                    .ThenInclude(c => c!.Organization)
                .FirstOrDefaultAsync(ac => ac.Code == code);

            if (authorizationCode == null)
            {
                _logger.LogWarning("使用済みマーク対象の認可コードが見つかりません: Code={Code}", code);
                return false;
            }

            if (authorizationCode.IsUsed)
            {
                _logger.LogWarning(
                    "既に使用済みの認可コードです: Code={Code}, UsedAt={UsedAt}",
                    code, authorizationCode.UsedAt);
                return false;
            }

            authorizationCode.IsUsed = true;
            authorizationCode.UsedAt = DateTimeOffset.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "認可コードを使用済みにマークしました: Code={Code}, UsedAt={UsedAt}",
                code, authorizationCode.UsedAt);

            return true;
        }

        public async Task<int> CleanupExpiredCodesAsync()
        {
            var expiredCodes = await _context.AuthorizationCodes
                .Include(ac => ac.Client)
                    .ThenInclude(c => c!.Organization)
                .Where(ac => ac.ExpiresAt <= DateTimeOffset.UtcNow)
                .ToListAsync();

            if (expiredCodes.Count == 0)
            {
                _logger.LogInformation("クリーンアップ対象の期限切れ認可コードはありません");
                return 0;
            }

            _context.AuthorizationCodes.RemoveRange(expiredCodes);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "期限切れ認可コードをクリーンアップしました: Count={Count}",
                expiredCodes.Count);

            return expiredCodes.Count;
        }

        public async Task<AuthorizationCodeStatistics> GetStatisticsAsync()
        {
            var now = DateTimeOffset.UtcNow;

            var totalCodes = await _context.AuthorizationCodes.CountAsync();
            var activeCodes = await _context.AuthorizationCodes
                .Where(ac => !ac.IsUsed && ac.ExpiresAt > now)
                .CountAsync();
            var expiredCodes = await _context.AuthorizationCodes
                .Where(ac => ac.ExpiresAt <= now)
                .CountAsync();
            var usedCodes = await _context.AuthorizationCodes
                .Where(ac => ac.IsUsed)
                .CountAsync();

            return new AuthorizationCodeStatistics
            {
                TotalCodes = totalCodes,
                ActiveCodes = activeCodes,
                ExpiredCodes = expiredCodes,
                UsedCodes = usedCodes
            };
        }
    }
}