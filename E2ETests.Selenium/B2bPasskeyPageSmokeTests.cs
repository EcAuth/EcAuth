using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace E2ETests.Selenium;

/// <summary>
/// B2B パスキーテストページ（wwwroot/b2b-passkey-test.html）の描画 smoke テスト。
///
/// 実ブラウザ（Chrome + ChromeDriver）で静的ページを開き、タイトル・見出し・主要な
/// フォーム要素がレンダリングされることを確認する。WebAuthn の署名検証そのものは
/// CDP Virtual Authenticator を使う Playwright 側 E2E に委ね、ここでは「ページが
/// 正しく配信され DOM が組み上がる」ことだけを軽量に検証する。
/// </summary>
public sealed class B2bPasskeyPageSmokeTests : IClassFixture<ChromeDriverFixture>
{
    private readonly ChromeDriverFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;

    public B2bPasskeyPageSmokeTests(ChromeDriverFixture fixture) => _fixture = fixture;

    [Fact]
    public void パスキーテストページが配信され主要要素が描画されること()
    {
        Driver.Navigate().GoToUrl($"{_fixture.BaseUrl}/b2b-passkey-test.html");

        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
        var heading = wait.Until(d => d.FindElement(By.TagName("h1")));

        Assert.Equal("B2B Passkey Test - EcAuth", Driver.Title);
        Assert.Contains("B2B Passkey Test Page", heading.Text);

        // 登録フォームの既定値が DOM に反映されていること（静的配信＋スクリプト初期化の最低限の確認）。
        Assert.Equal("client_id", Driver.FindElement(By.Id("clientId")).GetAttribute("value"));
        Assert.Equal("localhost", Driver.FindElement(By.Id("rpId")).GetAttribute("value"));

        // デバッグログ用の <pre id="debugLog"> が存在し、ページの土台が組み上がっていること。
        Assert.True(Driver.FindElement(By.Id("debugLog")).Displayed);
    }
}
