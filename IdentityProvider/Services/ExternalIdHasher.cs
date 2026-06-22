using IdpUtilities;

namespace IdentityProvider.Services
{
    /// <summary>
    /// B2BUser.external_id（EC-CUBE の login_id やメールアドレス等の個人情報を含み得る値）を
    /// 永続化・検索する際に適用するハッシュ化ヘルパー。
    ///
    /// 個人情報非保持要件（requirements 3.2.1）に従い、external_id は平文ではなく正規化のうえ
    /// SHA-256 ハッシュ化した値で保持・照合する。B2C の EmailHash と同一アルゴリズム
    /// （Trim + ToLowerInvariant → UTF-8 → SHA-256 → 大文字 hex 64 文字）を用いることで、
    /// account_owner（external_id = メールアドレス）と一般管理者（external_id = login_id）の
    /// 両経路で一貫したハッシュ値を得る。
    ///
    /// 正規化に ToLowerInvariant を含むため login_id は実質的に大文字小文字を区別しない
    /// （同一 Organization 内で大小のみ異なる login_id は同一ユーザーへ解決される）。
    /// </summary>
    public static class ExternalIdHasher
    {
        /// <summary>
        /// external_id（平文）を正規化 + SHA-256 ハッシュ化した値を返す。
        /// 値が null または空白の場合は <see cref="System.ArgumentException"/> を投げる。
        /// </summary>
        public static string Hash(string externalId)
        {
            return EmailHashUtil.HashEmail(externalId);
        }
    }
}
