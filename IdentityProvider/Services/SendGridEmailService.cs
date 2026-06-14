using SendGrid;
using SendGrid.Helpers.Mail;

namespace IdentityProvider.Services
{
    /// <inheritdoc cref="IEmailService" />
    public class SendGridEmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SendGridEmailService> _logger;

        public SendGridEmailService(IConfiguration configuration, ILogger<SendGridEmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task SendSignupConfirmationAsync(string toEmail, string organizationName, string confirmUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("toEmail cannot be null or empty.", nameof(toEmail));
            if (string.IsNullOrWhiteSpace(confirmUrl))
                throw new ArgumentException("confirmUrl cannot be null or empty.", nameof(confirmUrl));

            // API キーは IConfiguration（SendGrid:ApiKey）→ 環境変数（SENDGRID_API_KEY）の順で解決する。
            var apiKey = _configuration["SendGrid:ApiKey"]
                ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // 既存サービス（ExternalIdpTokenService 等）の流儀に合わせ、未設定時は警告ログを出して送信処理を中断する。
                _logger.LogWarning(
                    "SENDGRID_API_KEY が未設定のため申込確認メールを送信できません: To={ToEmail}",
                    toEmail);
                return;
            }

            // 送信元アドレス・表示名も同様に IConfiguration → 環境変数の順で解決する。
            var fromEmail = _configuration["SendGrid:FromEmail"]
                ?? Environment.GetEnvironmentVariable("SENDGRID_FROM_EMAIL")
                ?? "noreply@ecauth.jp";
            var fromName = _configuration["SendGrid:FromName"]
                ?? Environment.GetEnvironmentVariable("SENDGRID_FROM_NAME")
                ?? "EcAuth";

            var subject = "【EcAuth】お申し込み確認のお願い";

            var displayOrganization = string.IsNullOrWhiteSpace(organizationName)
                ? "ご登録の組織"
                : organizationName;

            var plainTextContent =
                $"EcAuth へのお申し込みありがとうございます。\n\n" +
                $"{displayOrganization} のお申し込みを受け付けました。\n" +
                $"以下の URL にアクセスして、お申し込み内容のご確認をお願いいたします。\n\n" +
                $"{confirmUrl}\n\n" +
                $"このメールにお心当たりがない場合は、お手数ですが破棄してください。\n\n" +
                $"-- \nEcAuth";

            var htmlContent =
                $"<p>EcAuth へのお申し込みありがとうございます。</p>" +
                $"<p>{System.Net.WebUtility.HtmlEncode(displayOrganization)} のお申し込みを受け付けました。<br>" +
                $"下記のボタン（リンク）から、お申し込み内容のご確認をお願いいたします。</p>" +
                $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(confirmUrl)}\">お申し込みを確認する</a></p>" +
                $"<p>ボタンが動作しない場合は、以下の URL をブラウザに貼り付けてアクセスしてください。<br>" +
                $"{System.Net.WebUtility.HtmlEncode(confirmUrl)}</p>" +
                $"<p>このメールにお心当たりがない場合は、お手数ですが破棄してください。</p>" +
                $"<p>--<br>EcAuth</p>";

            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(fromEmail, fromName);
            var to = new EmailAddress(toEmail);
            var message = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);

            var response = await client.SendEmailAsync(message, ct);

            if ((int)response.StatusCode >= 400)
            {
                var body = await response.Body.ReadAsStringAsync(ct);
                _logger.LogError(
                    "申込確認メールの送信に失敗しました: To={ToEmail}, StatusCode={StatusCode}, Body={Body}",
                    toEmail, (int)response.StatusCode, body);
                throw new InvalidOperationException(
                    $"SendGrid によるメール送信に失敗しました (StatusCode={(int)response.StatusCode})。");
            }

            _logger.LogInformation(
                "申込確認メールを送信しました: To={ToEmail}, Organization={Organization}",
                toEmail, displayOrganization);
        }
    }
}
