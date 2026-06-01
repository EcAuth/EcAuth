using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace E2ETests.Selenium;

/// <summary>
/// Selenium WebDriver（Chrome）を 1 テストクラスにつき 1 つ起動・破棄する xUnit フィクスチャ。
///
/// このプロジェクトは nanasess/setup-chromedriver@v3 で導入された ChromeDriver を
/// 「そのまま使う」ことを意図している。そのため Selenium Manager による
/// ドライバ自動ダウンロードには頼らず、PATH（または CHROMEWEBDRIVER）上の
/// chromedriver を明示的に解決して使用する。CI ではこの解決経路が action 由来の
/// バイナリを指す。ローカル開発で chromedriver が見つからない場合のみ、Selenium
/// Manager にフォールバックする。
/// </summary>
public sealed class ChromeDriverFixture : IDisposable
{
    /// <summary>テスト対象 IdP のベース URL。CI / ローカル Docker いずれも https://localhost:8081。</summary>
    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/') ?? "https://localhost:8081";

    public IWebDriver Driver { get; }

    public ChromeDriverFixture()
    {
        var options = new ChromeOptions();
        // CI ランナー / コンテナでの定番フラグ。--headless=new は Chrome 109+ の新ヘッドレス。
        options.AddArgument("--headless=new");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--window-size=1280,1024");
        // IdP はローカル/CI とも自己署名証明書の HTTPS で待ち受けるため証明書エラーを無視する。
        options.AcceptInsecureCertificates = true;

        var driverDirectory = ResolveChromeDriverDirectory();

        ChromeDriverService service = driverDirectory is null
            ? ChromeDriverService.CreateDefaultService()        // ローカル: Selenium Manager に委譲
            : ChromeDriverService.CreateDefaultService(driverDirectory); // CI: action 導入の chromedriver

        service.SuppressInitialDiagnosticInformation = true;
        service.HideCommandPromptWindow = true;

        Driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// setup-chromedriver が配置した chromedriver の「ディレクトリ」を解決する。
    /// 優先順位: CHROMEWEBDRIVER 環境変数 → PATH 上の探索。見つからなければ null。
    /// </summary>
    private static string? ResolveChromeDriverDirectory()
    {
        var explicitDir = Environment.GetEnvironmentVariable("CHROMEWEBDRIVER");
        if (!string.IsNullOrEmpty(explicitDir) && File.Exists(Path.Combine(explicitDir, ExecutableName)))
        {
            return explicitDir;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var dir in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, ExecutableName)))
            {
                return dir;
            }
        }

        return null;
    }

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "chromedriver.exe" : "chromedriver";

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }
}
