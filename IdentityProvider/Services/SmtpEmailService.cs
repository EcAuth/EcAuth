using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace IdentityProvider.Services
{
    /// <summary>
    /// MailKit ベースの SMTP メール送信実装。
    /// ローカル開発 / CI E2E で <c>Email:Provider=Smtp</c> のとき DI され、
    /// mailpit 等の SMTP サーバー（<c>Smtp:Host</c> / <c>Smtp:Port</c>）へ送信する。
    /// 本文生成は <see cref="EmailTemplates"/> に集約し、SendGrid と同一本文を保証する。
    /// </summary>
    public class SmtpEmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmtpEmailService> _logger;

        public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
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

            var content = EmailTemplates.BuildSignupConfirmation(organizationName, confirmUrl);
            await SendAsync(toEmail, content, ct);

            var displayOrganization = string.IsNullOrWhiteSpace(organizationName)
                ? "ご登録の組織"
                : organizationName;
            _logger.LogInformation(
                "申込確認メールを送信しました (SMTP): To={ToEmail}, Organization={Organization}",
                MaskEmail(toEmail), displayOrganization);
        }

        /// <inheritdoc />
        public async Task SendMagicLoginLinkAsync(string toEmail, string magicLinkUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("toEmail cannot be null or empty.", nameof(toEmail));
            if (string.IsNullOrWhiteSpace(magicLinkUrl))
                throw new ArgumentException("magicLinkUrl cannot be null or empty.", nameof(magicLinkUrl));

            var content = EmailTemplates.BuildMagicLoginLink(magicLinkUrl);
            await SendAsync(toEmail, content, ct);

            _logger.LogInformation(
                "マジックリンクを送信しました (SMTP): To={ToEmail}",
                MaskEmail(toEmail));
        }

        /// <summary>
        /// 共通の SMTP 送信処理。<c>Smtp:Host</c> / <c>Smtp:Port</c> へ接続し、
        /// プレーンテキスト + HTML の multipart メッセージを送信する。
        /// </summary>
        private async Task SendAsync(string toEmail, EmailTemplates.EmailContent content, CancellationToken ct)
        {
            // SMTP 接続先は IConfiguration（Smtp:Host / Smtp:Port）→ 環境変数の順で解決する。
            var host = _configuration["Smtp:Host"] ?? _configuration["SMTP_HOST"];
            if (string.IsNullOrWhiteSpace(host))
            {
                // Provider=Smtp を選んだのに接続先が未設定なのは設定不備。fail-closed で例外を投げる。
                _logger.LogError("Smtp:Host が未設定のためメールを送信できません: To={ToEmail}", MaskEmail(toEmail));
                throw new InvalidOperationException("SMTP ホスト（Smtp:Host / SMTP_HOST）が設定されていません。");
            }

            var portValue = _configuration["Smtp:Port"] ?? _configuration["SMTP_PORT"];
            var port = int.TryParse(portValue, out var parsedPort) ? parsedPort : 1025;

            // 送信元アドレス・表示名は SendGrid 実装と同じキーで解決する（両プロバイダで統一）。
            var fromEmail = _configuration["SendGrid:FromEmail"]
                ?? _configuration["SENDGRID_FROM_EMAIL"]
                ?? "noreply@ecauth.jp";
            var fromName = _configuration["SendGrid:FromName"]
                ?? _configuration["SENDGRID_FROM_NAME"]
                ?? "EcAuth";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = content.Subject;
            message.Body = new BodyBuilder
            {
                TextBody = content.PlainText,
                HtmlBody = content.Html
            }.ToMessageBody();

            using var client = new SmtpClient();
            // mailpit 等の開発用 SMTP は TLS 非対応。SecureSocketOptions.None で平文接続する。
            // 本番のメール送信は SendGrid（HTTP API）が担うため、この平文 SMTP は
            // ローカル / CI の閉じたネットワーク内でのみ使用される。
            await client.ConnectAsync(host, port, SecureSocketOptions.None, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }

        /// <summary>
        /// ログ出力用にメールアドレスをマスクする（<see cref="SendGridEmailService"/> と同一仕様）。
        /// 例: <c>owner@example.com</c> → <c>o****@example.com</c>。
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
                return "****";
            }

            var local = email[..atIndex];
            var domain = email[atIndex..];
            var visible = local[..1];
            return $"{visible}****{domain}";
        }
    }
}
