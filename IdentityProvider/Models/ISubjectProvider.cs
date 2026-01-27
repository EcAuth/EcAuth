namespace IdentityProvider.Models
{
    /// <summary>
    /// Subject を持つエンティティの共通インターフェイス。
    /// EcAuthUser（B2C）と B2BUser（B2B）の両方で使用される。
    /// TokenRequest で統一的にユーザーを扱うために使用する。
    /// </summary>
    public interface ISubjectProvider
    {
        /// <summary>
        /// ユーザーの一意識別子（UUID形式）
        /// </summary>
        string Subject { get; }
    }
}
