using IdentityProvider.Services;
using Xunit;

namespace IdentityProvider.Test.Services
{
    /// <summary>
    /// メール本文の共通テンプレート（<see cref="EmailTemplates"/>）の検証。
    /// SendGrid / SMTP の両プロバイダがこの生成結果を共有するため、本文のドリフトを
    /// このテストで固定する。
    /// </summary>
    public class EmailTemplatesTests
    {
        [Fact]
        public void BuildSignupConfirmation_件名と確認URLを含む()
        {
            var content = EmailTemplates.BuildSignupConfirmation("テスト商店", "https://accounts.ec-auth.io/signup/confirm?token=abc123");

            Assert.Equal("【EcAuth】お申し込み確認のお願い", content.Subject);
            Assert.Contains("テスト商店", content.PlainText);
            Assert.Contains("https://accounts.ec-auth.io/signup/confirm?token=abc123", content.PlainText);
            Assert.Contains("https://accounts.ec-auth.io/signup/confirm?token=abc123", content.Html);
            // HTML では anchor として埋め込まれる
            Assert.Contains("<a href=", content.Html);
        }

        [Fact]
        public void BuildSignupConfirmation_組織名が空なら既定表記に置換される()
        {
            var content = EmailTemplates.BuildSignupConfirmation("  ", "https://example.com/signup/confirm?token=x");

            Assert.Contains("ご登録の組織", content.PlainText);
            Assert.Contains("ご登録の組織", content.Html);
        }

        [Fact]
        public void BuildSignupConfirmation_HTML本文で特殊文字がエスケープされる()
        {
            // 組織名・URL に含まれる特殊文字が HTML エスケープされ、生の < > & が残らないこと。
            var content = EmailTemplates.BuildSignupConfirmation("A&B <Shop>", "https://example.com/confirm?token=a&b=c");

            Assert.Contains("A&amp;B &lt;Shop&gt;", content.Html);
            Assert.DoesNotContain("<Shop>", content.Html);
            // プレーンテキストはエスケープしない（そのまま）
            Assert.Contains("A&B <Shop>", content.PlainText);
        }

        [Fact]
        public void BuildMagicLoginLink_件名とログインURLを含む()
        {
            var content = EmailTemplates.BuildMagicLoginLink("https://ec-auth.io/signin/magic-link?token=xyz789");

            Assert.Equal("【EcAuth】ログインリンクのお知らせ", content.Subject);
            Assert.Contains("https://ec-auth.io/signin/magic-link?token=xyz789", content.PlainText);
            Assert.Contains("https://ec-auth.io/signin/magic-link?token=xyz789", content.Html);
            Assert.Contains("10 分", content.PlainText);
            Assert.Contains("一度のみ", content.PlainText);
        }
    }
}
