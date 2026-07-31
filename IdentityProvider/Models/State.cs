namespace IdentityProvider.Models
{
    public class State
    {
        public int OpenIdProviderId { get; set; }
        public string RedirectUri { get; set; } = string.Empty;
        public int ClientId { get; set; }
        public int OrganizationId { get; set; }
        public string? Scope { get; set; }
        /// <summary>
        /// クライアントから受け取った state パラメータ。
        /// RFC 6749 Section 4.1.2 に準拠し、コールバック時にそのまま返す必要がある。
        /// </summary>
        public string? ClientState { get; set; }
        /// <summary>
        /// PKCE (RFC 7636) の code_challenge。認可リクエストで受け取る場所（/v1/authorization）と
        /// 認可コードを発行する場所（/v1/auth/callback）が外部 IdP の往復を挟んで分かれるため、
        /// Iron で封緘された本 State に載せて往復させる（改ざん耐性を得る）。
        /// </summary>
        public string? CodeChallenge { get; set; }
        /// <summary>
        /// PKCE の code_challenge_method。本 IdP は "S256" のみサポートする。
        /// </summary>
        public string? CodeChallengeMethod { get; set; }
    }
}
