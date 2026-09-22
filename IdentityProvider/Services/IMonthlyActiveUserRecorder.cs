namespace IdentityProvider.Services
{
    /// <summary>
    /// アクセストークン発行時に <c>monthly_active_user</c> へ MAU を記録する（EcAuthDocs#45）。
    ///
    /// <para>
    /// <b>契約: 例外を投げない。</b> 集計の失敗で認証を落とさないため、DB エラーを含むあらゆる例外は実装内で
    /// 握ってログに出す（ユニーク制約違反 = レースは Debug、それ以外は Error）。呼び出し側で try-catch を
    /// 書かない前提なので、実装を差し替えるときもこの契約を守ること。
    /// </para>
    /// <para>
    /// 記録しない条件は構造的で不変なものだけ（記録行は write-once なので、後からポリシーで変わる除外は
    /// 集計側 <see cref="IUsageReportService"/> に置く）:
    /// <list type="bullet">
    /// <item><see cref="Models.SubjectType.Account"/> — EcAuth 自身のマイページ利用者。課金対象になりえない</item>
    /// <item><see cref="ITokenService.TokenRequest.GrantType"/> が <see cref="GrantType.AuthorizationCode"/> 以外 —
    /// refresh やマジックリンクによる発行は人間の認証行為ではない</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IMonthlyActiveUserRecorder
    {
        /// <summary>
        /// 発行済みトークンの主体を当月の MAU として記録する。既に記録済みなら何もしない。
        /// </summary>
        /// <param name="request">トークン発行リクエスト（Client / SubjectType / GrantType / AuthMethod を参照する）</param>
        /// <param name="subject">発行先の subject</param>
        /// <param name="issuedAt">トークン発行時刻（UTC）。対象月の決定と <c>first_seen_at</c> に使う</param>
        /// <param name="cancellationToken">
        /// 通常は <see cref="CancellationToken.None"/> を渡す。トークン返却直後にクライアントが切断しても
        /// 記録だけが落ちる事態を避けるため
        /// </param>
        Task RecordAsync(ITokenService.TokenRequest request, string subject, DateTimeOffset issuedAt, CancellationToken cancellationToken);
    }
}
