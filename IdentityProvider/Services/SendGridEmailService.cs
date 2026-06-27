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
                ?? _configuration["SENDGRID_API_KEY"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // API キー未設定で return（成功扱い）すると、申込者には「確認メール送信済み」と
                // 案内されるのにメールが届かず、申込が完了不能になる。設定不備として例外を投げて
                // 呼び出し側（500）に伝播させる（fail-closed）。
                _logger.LogError(
                    "SENDGRID_API_KEY が未設定のため申込確認メールを送信できません: To={ToEmail}",
                    MaskEmail(toEmail));
                throw new InvalidOperationException(
                    "SendGrid API キー（SendGrid:ApiKey / SENDGRID_API_KEY）が設定されていません。");
            }

            // 送信元アドレス・表示名も同様に IConfiguration → 環境変数の順で解決する。
            var fromEmail = _configuration["SendGrid:FromEmail"]
                ?? _configuration["SENDGRID_FROM_EMAIL"]
                ?? "noreply@ecauth.jp";
            var fromName = _configuration["SendGrid:FromName"]
                ?? _configuration["SENDGRID_FROM_NAME"]
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
                // レスポンス Body には宛先や内部情報が含まれ得るため全文はログに出さず、
                // 宛先はマスクし、ステータスコードのみを記録する。
                _logger.LogError(
                    "申込確認メールの送信に失敗しました: To={ToEmail}, StatusCode={StatusCode}",
                    MaskEmail(toEmail), (int)response.StatusCode);
                throw new InvalidOperationException(
                    $"SendGrid によるメール送信に失敗しました (StatusCode={(int)response.StatusCode})。");
            }

            _logger.LogInformation(
                "申込確認メールを送信しました: To={ToEmail}, Organization={Organization}",
                MaskEmail(toEmail), displayOrganization);
        }

        /// <inheritdoc />
        public async Task SendMagicLoginLinkAsync(string toEmail, string magicLinkUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("toEmail cannot be null or empty.", nameof(toEmail));
            if (string.IsNullOrWhiteSpace(magicLinkUrl))
                throw new ArgumentException("magicLinkUrl cannot be null or empty.", nameof(magicLinkUrl));

            // API キーは IConfiguration（SendGrid:ApiKey）→ 環境変数（SENDGRID_API_KEY）の順で解決する。
            var apiKey = _configuration["SendGrid:ApiKey"]
                ?? _configuration["SENDGRID_API_KEY"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // API キー未設定でメールが届かないと、利用者はログインリンクを受け取れずログイン不能になる。
                // 設定不備として例外を投げ、呼び出し側（500）に伝播させる（fail-closed）。
                _logger.LogError(
                    "SENDGRID_API_KEY が未設定のためマジックリンクを送信できません: To={ToEmail}",
                    MaskEmail(toEmail));
                throw new InvalidOperationException(
                    "SendGrid API キー（SendGrid:ApiKey / SENDGRID_API_KEY）が設定されていません。");
            }

            var fromEmail = _configuration["SendGrid:FromEmail"]
                ?? _configuration["SENDGRID_FROM_EMAIL"]
                ?? "noreply@ecauth.jp";
            var fromName = _configuration["SendGrid:FromName"]
                ?? _configuration["SENDGRID_FROM_NAME"]
                ?? "EcAuth";

            var subject = "【EcAuth】ログインリンクのお知らせ";

            var plainTextContent =
                $"EcAuth へのログインリクエストを受け付けました。\n\n" +
                $"以下の URL にアクセスしてログインを完了してください（有効期限: 10 分）。\n\n" +
                $"{magicLinkUrl}\n\n" +
                $"このリンクは一度のみ使用できます。\n" +
                $"心当たりがない場合は、このメールを破棄してください（操作は不要です）。\n\n" +
                $"-- \nEcAuth";

            var htmlContent =
                $"<p>EcAuth へのログインリクエストを受け付けました。</p>" +
                $"<p>下記のボタン（リンク）からログインを完了してください（有効期限: 10 分）。</p>" +
                $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(magicLinkUrl)}\">ログインする</a></p>" +
                $"<p>ボタンが動作しない場合は、以下の URL をブラウザに貼り付けてアクセスしてください。<br>" +
                $"{System.Net.WebUtility.HtmlEncode(magicLinkUrl)}</p>" +
                $"<p>このリンクは一度のみ使用できます。<br>" +
                $"心当たりがない場合は、このメールを破棄してください（操作は不要です）。</p>" +
                $"<p>--<br>EcAuth</p>";

            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(fromEmail, fromName);
            var to = new EmailAddress(toEmail);
            var message = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);

            var response = await client.SendEmailAsync(message, ct);

            if ((int)response.StatusCode >= 400)
            {
                _logger.LogError(
                    "マジックリンクの送信に失敗しました: To={ToEmail}, StatusCode={StatusCode}",
                    MaskEmail(toEmail), (int)response.StatusCode);
                throw new InvalidOperationException(
                    $"SendGrid によるメール送信に失敗しました (StatusCode={(int)response.StatusCode})。");
            }

            _logger.LogInformation(
                "マジックリンクを送信しました: To={ToEmail}",
                MaskEmail(toEmail));
        }

        /// <summary>
        /// ログ出力用にメールアドレスをマスクする。ローカル部の先頭 1 文字のみ残し、
        /// 残りを伏字にしたうえでドメインを保持する（例: <c>owner@example.com</c> → <c>o****@example.com</c>）。
        /// </summary>
        private static string MaskEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return "(empty)";
            }

            var atIndex = email.IndexOf('@');
            if (atIndex <= 0)
            {
                // ローカル部が取得できない不正な形式は全体を伏字にする。
                return "****";
            }

            var local = email[..atIndex];
            var domain = email[atIndex..];
            var visible = local[..1];
            return $"{visible}****{domain}";
        }
    }
}
