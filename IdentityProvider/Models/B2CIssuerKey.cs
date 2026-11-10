namespace IdentityProvider.Models
{
    /// <summary>
    /// <see cref="EcAuthUserIdentity.IssuerKey"/>（B2C の発行元識別子）の記法（EcAuthDocs#122）。
    ///
    /// | 識別子の種類 | issuer_key | external_id（ハッシュ前） |
    /// |---|---|---|
    /// | ソーシャルログイン | <c>idp:{open_id_provider.id}</c> | IdP の sub |
    /// | EC-CUBE 会員（Phase 2） | <c>client:{client_id}</c> | <c>customer:{customer_id}</c> |
    ///
    /// ソーシャルログインを provider 名ではなく OpenIdProvider の行で区切るのは、行が Client に
    /// 紐づくため（provider 名は Client 間で衝突しうる）。<c>client:</c> は B2B の
    /// <see cref="B2BIssuerKey.ForClient"/> と同じ記法で、テーブルが別なので値が同じでも衝突しない。
    /// </summary>
    public static class B2CIssuerKey
    {
        public const string IdpPrefix = "idp:";

        /// <summary>ソーシャルログイン（外部 IdP）の発行元識別子を組み立てる。</summary>
        /// <param name="openIdProviderId"><see cref="OpenIdProvider.Id"/>。</param>
        public static string ForIdp(int openIdProviderId)
        {
            if (openIdProviderId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(openIdProviderId), "openIdProviderId は正の値です。");
            }
            return IdpPrefix + openIdProviderId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>EC-CUBE 会員など、Client が発行する識別子の発行元識別子を組み立てる。</summary>
        public static string ForClient(string clientId) => B2BIssuerKey.ForClient(clientId);
    }
}
