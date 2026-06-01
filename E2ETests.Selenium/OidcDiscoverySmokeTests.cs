using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace E2ETests.Selenium;

/// <summary>
/// OIDC Discovery / JWKS エンドポイントのブラウザ smoke テスト。
///
/// 厳密な Provider Metadata 検証（省略フィールドの不在チェック等）は Playwright 側の
/// oidc_discovery.spec.ts が担う。ここでは実ブラウザで JSON エンドポイントを開き、
/// 主要キーがレスポンス本文に現れることだけを軽量に確認する（配信経路の生存確認）。
/// Chrome は application/json をビルトインの JSON ビューアで描画するため、
/// document.body.innerText に整形済み JSON テキストが含まれる。
/// </summary>
public sealed class OidcDiscoverySmokeTests : IClassFixture<ChromeDriverFixture>
{
    private readonly ChromeDriverFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;

    public OidcDiscoverySmokeTests(ChromeDriverFixture fixture) => _fixture = fixture;

    private string BodyText()
    {
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
        return wait.Until(d =>
        {
            var text = ((IJavaScriptExecutor)d).ExecuteScript("return document.body.innerText;") as string;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        })!;
    }

    [Fact]
    public void Discovery_が_Provider_Metadata_を返すこと()
    {
        Driver.Navigate().GoToUrl($"{_fixture.BaseUrl}/.well-known/openid-configuration");

        var body = BodyText();

        Assert.Contains("\"issuer\"", body);
        Assert.Contains(_fixture.BaseUrl, body);
        Assert.Contains("\"token_endpoint\"", body);
        Assert.Contains("\"userinfo_endpoint\"", body);
        Assert.Contains("\"jwks_uri\"", body);
    }

    [Fact]
    public void JWKS_が_RS256_公開鍵を配信すること()
    {
        Driver.Navigate().GoToUrl($"{_fixture.BaseUrl}/.well-known/jwks.json");

        var body = BodyText();

        Assert.Contains("\"keys\"", body);
        Assert.Contains("\"RSA\"", body);
        Assert.Contains("\"RS256\"", body);
    }
}
