namespace IdentityProvider.Services
{
    /// <summary>
    /// トークンがどの経路で発行されるかを表す。<see cref="ITokenService.TokenRequest.GrantType"/> で必須。
    ///
    /// MAU の記録（<see cref="IMonthlyActiveUserRecorder"/>）は <see cref="AuthorizationCode"/> のときだけ行う。
    /// MAU は「当月に人間の認証行為を経てトークン発行されたユーザー」と定義され（EcAuthDocs#45 / #119）、
    /// refresh による再発行は人間のログインではないため計上しない。この除外は記録行が write-once であるため
    /// 集計時ではなく記録時に確定させる必要があり、発行経路の申告を必須にしている。
    /// </summary>
    public enum GrantType
    {
        /// <summary>
        /// <c>grant_type=authorization_code</c>。認可コードの発行元（パスキー assertion / 外部 IdP 認証 / SSO）が
        /// 人間の認証行為なので MAU に計上する。
        /// </summary>
        AuthorizationCode,

        /// <summary>
        /// <c>grant_type=refresh_token</c>（EcAuth#339）。人間のログインを伴わないため MAU に計上しない。
        /// </summary>
        RefreshToken,

        /// <summary>
        /// マジックリンク（<see cref="MagicLinkService"/>）。認可コードを経ずに Account のトークンを直接発行する。
        /// Account は課金対象外なので MAU に計上しない。
        /// </summary>
        MagicLink
    }
}
