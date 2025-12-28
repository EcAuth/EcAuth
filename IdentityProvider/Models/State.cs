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
    }
}
