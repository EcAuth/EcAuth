namespace IdentityProvider.Exceptions
{
    /// <summary>
    /// 申込リクエスト（<c>POST /api/signup/request</c>）または申込確認（<c>POST /api/signup/confirm</c>）の
    /// バリデーション違反を表す例外。
    /// <para>
    /// Controller は <see cref="Error"/> / <see cref="ErrorDescription"/> / <see cref="Field"/> を
    /// レスポンスボディに展開し、<see cref="StatusCode"/>（リクエスト時は 422、confirm 時の code 衝突は 409）を
    /// HTTP ステータスとして返すことを想定する。設計書「申込リクエストのバリデーション」を参照。
    /// </para>
    /// </summary>
    public class SignupValidationException : Exception
    {
        /// <summary>
        /// 機械可読なエラーコード（例: <c>invalid_email</c> / <c>disposable_email</c> /
        /// <c>invalid_organization_name</c> / <c>invalid_site_url</c> /
        /// <c>organization_already_exists</c> / <c>unsupported_version</c>）。
        /// </summary>
        public string Error { get; }

        /// <summary>
        /// 申込者に表示する説明文（日本語）。
        /// </summary>
        public string ErrorDescription { get; }

        /// <summary>
        /// エラーの原因となった入力フィールド名（該当しない場合は null）。
        /// </summary>
        public string? Field { get; }

        /// <summary>
        /// HTTP ステータスコード。リクエスト時のバリデーションは 422、
        /// confirm 時の組織コード衝突（Race Condition）は 409 を表す。
        /// </summary>
        public int StatusCode { get; }

        public SignupValidationException(string error, string errorDescription, string? field = null, int statusCode = 422)
            : base($"{error}: {errorDescription}")
        {
            Error = error;
            ErrorDescription = errorDescription;
            Field = field;
            StatusCode = statusCode;
        }
    }
}
