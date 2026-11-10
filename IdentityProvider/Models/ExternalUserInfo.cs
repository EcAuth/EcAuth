namespace IdentityProvider.Models
{
    /// <summary>
    /// 外部IdPから取得したユーザー情報を統一的に扱うためのモデル
    /// </summary>
    public class ExternalUserInfo
    {
        /// <summary>
        /// 外部IdPのユーザーID
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// メールアドレス（オプション）
        /// </summary>
        public string? Email { get; set; }

        /// <summary>
        /// IdP がメールアドレスを確認済みと表明しているか（OIDC 標準クレーム <c>email_verified</c>）。
        /// 表明が無いときは false。確認済みメールアドレス同士だけ自動で統合する（EcAuthDocs#122）。
        /// </summary>
        public bool EmailVerified { get; set; }

        /// <summary>
        /// 表示名（オプション）
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// プロバイダー名（google, line等）
        /// </summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// その他のクレーム
        /// </summary>
        public Dictionary<string, object>? Claims { get; set; }
    }
}