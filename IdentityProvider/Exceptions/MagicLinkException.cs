namespace IdentityProvider.Exceptions
{
    /// <summary>
    /// マジックリンクログイン（<c>POST /api/account/magic-link/request</c> /
    /// <c>POST /api/account/magic-link/verify</c>）の処理エラーを表す例外。
    /// <para>
    /// Controller は <see cref="Error"/> / <see cref="ErrorDescription"/> を
    /// レスポンスボディに展開し、<see cref="StatusCode"/> を HTTP ステータスとして返す。
    /// レート制限超過は 429、トークン検証失敗（無効／期限切れ／使用済みを区別しない）は 400 を想定する。
    /// </para>
    /// <para>
    /// Email enumeration 対策のため、トークン検証失敗時のメッセージは原因（無効／期限切れ／使用済み）を
    /// 区別せず、レート制限は Account 存在有無に関わらず同一閾値・同一レスポンスとする。
    /// </para>
    /// </summary>
    public class MagicLinkException : Exception
    {
        /// <summary>機械可読なエラーコード（例: <c>rate_limited</c> / <c>invalid_token</c>）。</summary>
        public string Error { get; }

        /// <summary>利用者に表示する説明文（日本語）。</summary>
        public string ErrorDescription { get; }

        /// <summary>HTTP ステータスコード（レート制限は 429、トークン検証失敗は 400）。</summary>
        public int StatusCode { get; }

        public MagicLinkException(string error, string errorDescription, int statusCode = 400)
            : base($"{error}: {errorDescription}")
        {
            Error = error;
            ErrorDescription = errorDescription;
            StatusCode = statusCode;
        }
    }
}
