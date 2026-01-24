namespace IdentityProvider.Models
{
    /// <summary>
    /// ユーザータイプを表す列挙型。
    /// AccessToken と AuthorizationCode で使用され、
    /// どの種類のユーザーに発行されたトークン/コードかを識別する。
    /// </summary>
    public enum SubjectType
    {
        /// <summary>
        /// B2C（エンドユーザー向け）: ECサイト顧客
        /// EcAuthUser エンティティに対応
        /// </summary>
        B2C = 0,

        /// <summary>
        /// B2B（管理者向け）: EC-CUBE管理者
        /// B2BUser エンティティに対応
        /// </summary>
        B2B = 1,

        /// <summary>
        /// Account（EcAuth管理者）: EcAuthサービス自体の管理者
        /// Account エンティティに対応
        /// </summary>
        Account = 2
    }
}
