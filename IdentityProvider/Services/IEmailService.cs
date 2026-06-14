namespace IdentityProvider.Services
{
    /// <summary>
    /// 申込フロー等で利用するメール送信サービス。
    /// 送信基盤（SendGrid 等）の実装詳細を隠蔽する。
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Account 申込時の確認メールを送信する。
        /// 確認リンク（<paramref name="confirmUrl"/>）を本文に含め、申込者にメールアドレスの到達性確認を促す。
        /// </summary>
        /// <param name="toEmail">送信先メールアドレス（申込者）</param>
        /// <param name="organizationName">申込対象の Organization 名（本文に表示）</param>
        /// <param name="confirmUrl">申込確認用 URL</param>
        /// <param name="ct">キャンセルトークン</param>
        Task SendSignupConfirmationAsync(string toEmail, string organizationName, string confirmUrl, CancellationToken ct = default);
    }
}
