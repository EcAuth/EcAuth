using Microsoft.Extensions.Configuration;

namespace IdentityProvider.Security
{
    /// <summary>
    /// PKCE (RFC 7636) の適用ポリシー。OAuth 2.1 準拠のため全 client に S256 を必須とする。
    ///
    /// 必須化により、code_challenge を伴わない認可コードは発行段階で拒否され、
    /// トークン交換でも拒否される。未更新のクライアントが残っている状態で有効にすると
    /// そのクライアントのログインが停止するため、ロールバック用のキルスイッチを設けている。
    ///
    /// 非秘密のチューニング定数のため配線（Terraform / CI / .env）には入れず、
    /// 障害時のみ appsettings/env で上書きする（CLAUDE.md の配線ルール参照）。
    /// 例: <c>Pkce__Required=false</c> で必須化前の挙動（confidential client は PKCE 任意）に戻る。
    /// </summary>
    public static class PkcePolicy
    {
        /// <summary>
        /// 必須化のコード既定値。true = 全 client に PKCE 必須。
        /// </summary>
        public const bool DefaultRequired = true;

        /// <summary>
        /// 設定キー。<see cref="DefaultRequired"/> を appsettings / 環境変数で上書きできる。
        /// </summary>
        public const string RequiredConfigKey = "Pkce:Required";

        /// <summary>
        /// PKCE が必須かどうかを返す。
        /// </summary>
        public static bool IsRequired(IConfiguration configuration)
        {
            return configuration.GetValue(RequiredConfigKey, DefaultRequired);
        }
    }
}
