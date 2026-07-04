using System.Net;

namespace IdentityProvider.Services
{
    /// <summary>
    /// メール本文（件名・プレーンテキスト・HTML）の生成を一元管理する。
    /// <see cref="SendGridEmailService"/>（本番 / staging）と <see cref="SmtpEmailService"/>
    /// （ローカル開発 / CI E2E）の両プロバイダが同一の本文を送信できるよう、
    /// 文面をここに集約してドリフトを防ぐ。
    /// </summary>
    public static class EmailTemplates
    {
        /// <summary>件名・プレーンテキスト・HTML をまとめて表す。</summary>
        public readonly record struct EmailContent(string Subject, string PlainText, string Html);

        /// <summary>
        /// Account 申込確認メールの本文を生成する。
        /// </summary>
        /// <param name="organizationName">申込対象の Organization 名（空の場合は既定表記に置換）</param>
        /// <param name="confirmUrl">申込確認用 URL</param>
        public static EmailContent BuildSignupConfirmation(string organizationName, string confirmUrl)
        {
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
                $"<p>{WebUtility.HtmlEncode(displayOrganization)} のお申し込みを受け付けました。<br>" +
                $"下記のボタン（リンク）から、お申し込み内容のご確認をお願いいたします。</p>" +
                $"<p><a href=\"{WebUtility.HtmlEncode(confirmUrl)}\">お申し込みを確認する</a></p>" +
                $"<p>ボタンが動作しない場合は、以下の URL をブラウザに貼り付けてアクセスしてください。<br>" +
                $"{WebUtility.HtmlEncode(confirmUrl)}</p>" +
                $"<p>このメールにお心当たりがない場合は、お手数ですが破棄してください。</p>" +
                $"<p>--<br>EcAuth</p>";

            return new EmailContent(subject, plainTextContent, htmlContent);
        }

        /// <summary>
        /// マジックリンクログイン用メールの本文を生成する。
        /// </summary>
        /// <param name="magicLinkUrl">マジックリンクのログイン URL</param>
        public static EmailContent BuildMagicLoginLink(string magicLinkUrl)
        {
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
                $"<p><a href=\"{WebUtility.HtmlEncode(magicLinkUrl)}\">ログインする</a></p>" +
                $"<p>ボタンが動作しない場合は、以下の URL をブラウザに貼り付けてアクセスしてください。<br>" +
                $"{WebUtility.HtmlEncode(magicLinkUrl)}</p>" +
                $"<p>このリンクは一度のみ使用できます。<br>" +
                $"心当たりがない場合は、このメールを破棄してください（操作は不要です）。</p>" +
                $"<p>--<br>EcAuth</p>";

            return new EmailContent(subject, plainTextContent, htmlContent);
        }
    }
}
