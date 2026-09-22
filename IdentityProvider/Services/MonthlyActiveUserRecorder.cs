using IdentityProvider.Data;
using IdentityProvider.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IMonthlyActiveUserRecorder"/>
    public class MonthlyActiveUserRecorder : IMonthlyActiveUserRecorder
    {
        /// <summary>
        /// <see cref="ITokenService.TokenRequest.AuthMethod"/> が未指定のときの推論値。
        /// B2B SSO（EcAuthDocs#123）を入れるときはこの推論を認可コード由来の明示値に置き換えること。
        /// </summary>
        public const string AuthMethodB2BPasskey = "b2b_passkey";
        public const string AuthMethodB2CSocial = "b2c_social";

        private readonly EcAuthDbContext _context;
        private readonly ILogger<MonthlyActiveUserRecorder> _logger;

        public MonthlyActiveUserRecorder(EcAuthDbContext context, ILogger<MonthlyActiveUserRecorder> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task RecordAsync(ITokenService.TokenRequest request, string subject, DateTimeOffset issuedAt, CancellationToken cancellationToken)
        {
            if (request.SubjectType == SubjectType.Account || request.GrantType != GrantType.AuthorizationCode)
            {
                return;
            }

            // TokenService.GenerateAccessTokenAsync が既に null を弾いているが、契約上ここで投げるわけにはいかない。
            if (request.Client.OrganizationId is not int organizationId)
            {
                _logger.LogError("MAU not recorded: client {ClientId} has no organization (subject {Subject})",
                    request.Client.Id, subject);
                return;
            }

            var yearMonth = UsageMonth.FromInstant(issuedAt).Value;
            var clientId = request.Client.Id;
            var subjectType = (int)request.SubjectType;
            var authMethod = request.AuthMethod ?? InferAuthMethod(request.SubjectType);

            try
            {
                // 1 ラウンドトリップの write-once。2 回目以降のログインは UNIQUE index の seek だけで書き込みゼロ。
                // MERGE や UPDLOCK/HOLDLOCK は使わない（範囲ロックのデッドロックを避ける。レースは UNIQUE index が最終防衛線）。
                // SaveChangesAsync とは別コマンド（別トランザクション）なので、失敗しても発行済みトークンには影響しない。
                // スキーマ修飾を付けないのは、SQL Server の既定スキーマ dbo とテスト（SQLite）の両方で同じ文を使うため。
                await _context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO monthly_active_user (year_month, client_id, organization_id, subject, subject_type, auth_method, first_seen_at)
                    SELECT {yearMonth}, {clientId}, {organizationId}, {subject}, {subjectType}, {authMethod}, {issuedAt}
                    WHERE NOT EXISTS (
                        SELECT 1 FROM monthly_active_user
                        WHERE year_month = {yearMonth} AND client_id = {clientId} AND subject = {subject})
                    """, cancellationToken);
            }
            catch (SqlException ex) when (DatabaseResilience.IsUniqueConstraintViolation(ex))
            {
                // 同一ユーザーの同時ログインで NOT EXISTS をすり抜けた側。行は既にあるので結果は正しい。
                _logger.LogDebug("MAU already recorded by a concurrent request: {YearMonth} client {ClientId} subject {Subject}",
                    yearMonth, clientId, subject);
            }
            catch (Exception ex)
            {
                // 課金データの欠落をサイレントにしない。ただし認証は落とさない（interface の契約）。
                _logger.LogError(ex, "Failed to record MAU: {YearMonth} client {ClientId} subject {Subject} (type: {SubjectType})",
                    yearMonth, clientId, subject, request.SubjectType);
            }
        }

        private static string InferAuthMethod(SubjectType subjectType)
        {
            return subjectType == SubjectType.B2B ? AuthMethodB2BPasskey : AuthMethodB2CSocial;
        }
    }
}
