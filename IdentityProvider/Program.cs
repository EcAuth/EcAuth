using Fido2NetLib;
using IdentityProvider.Constants;
using IdentityProvider.Data;
using IdentityProvider.Data.Seeders;
using IdentityProvider.Filters;
using IdentityProvider.Middlewares;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Asp.Versioning;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

// リバースプロキシ（Cloudflare）からの実クライアント IP 復元設定。
// 本番 API ホストは Cloudflare プロキシ配下にあり、さらに Azure App Service のプラットフォーム
// フロントエンドが TLS を終端して Kestrel へ転送する。Kestrel から見た「直前ホップ」は Cloudflare
// エッジではなく Azure フロントエンドになる点に注意。
//
// ForwardedHeaders ミドルウェアは、直前ホップの IP が信頼済み（KnownIPNetworks / KnownProxies に
// 含まれる）かどうかで転送ヘッダ適用の全体（For も Proto も）をゲートする。Cloudflare の CIDR だけを
// 登録すると、直前ホップ（Azure フロントエンド）が信頼外となり For/Proto いずれも適用されず、
//   - scheme が http のままになり OIDC discovery の issuer / jwks_uri が http:// で生成され、
//     JWKS が http→https リダイレクト 301 を返してフェデレーションが壊れる
//   - CF-Connecting-IP も復元されず IP ベースのレート制限が機能しない
// という二重の不具合になる（PR #418 / #429 で発生）。
//
// KnownIPNetworks / KnownProxies を両方空にするとトラストチェック自体が無効化され、全フォワーダを
// 信頼する（Azure 既定の ASPNETCORE_FORWARDEDHEADERS_ENABLED と同じ挙動）。これにより
//   - X-Forwarded-Proto から scheme=https を復元（issuer / jwks_uri が https に戻る）
//   - CF-Connecting-IP から実クライアント IP を復元（IP レート制限 / App Insights の client_IP）
// が成立する。CF-Connecting-IP 偽装によるレート制限回避は、本番のオリジンロック
// （Azure access restriction = Cloudflare IP 限定、ecauth-infrastructure #120）で防ぐ。これが
// 唯一かつ本来の信頼境界（旧 CloudflareOptions による KnownIPNetworks 限定は冗長な上、上記の
// 直前ホップ問題で実際には機能していなかったため撤去した）。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Cloudflare は実クライアント IP を CF-Connecting-IP に単一値で格納する（X-Forwarded-For は多段で連なる）。
    options.ForwardedForHeaderName = "CF-Connecting-IP";
    options.ForwardLimit = null;

    // 直前ホップ（Azure フロントエンド）を信頼対象に含めるため、トラストチェックを無効化する。
    // 信頼境界は本番オリジンロック（#120）に一本化する。
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddScoped<IIssuerResolver, IssuerResolver>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthorizationCodeService, AuthorizationCodeService>();
builder.Services.AddScoped<IExternalIdpTokenService, ExternalIdpTokenService>();
builder.Services.AddScoped<IExternalUserInfoService, ExternalUserInfoService>();
builder.Services.AddScoped<OrganizationFilter>();
builder.Services.AddHttpClient(); // HttpClientFactoryを有効化（ExternalUserInfoServiceで使用）

// B2Bパスキー認証関連サービス
builder.Services.AddScoped<IWebAuthnChallengeService, WebAuthnChallengeService>();
builder.Services.AddScoped<IB2BUserService, B2BUserService>();
builder.Services.AddScoped<IB2BPasskeyService, B2BPasskeyService>();

// Account 申込フロー（Phase D-1）関連サービス
builder.Services.AddScoped<ISignupService, SignupService>();
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
// ブロックリストは不変のため singleton 登録（DisposableEmailChecker の設計）
builder.Services.AddSingleton<IDisposableEmailChecker, DisposableEmailChecker>();

// マジックリンクログイン（Phase D-2）関連サービス
// 時間・回数ポリシーは MagicLinkOptions に集約。未設定でも既定値で動作し、
// 構成セクション "MagicLink"（例: MagicLink__RetentionDays）があれば上書きされる。
builder.Services.Configure<MagicLinkOptions>(
    builder.Configuration.GetSection(MagicLinkOptions.SectionName));
builder.Services.AddScoped<IMagicLinkService, MagicLinkService>();
// 期限切れトークンの日次クリーンアップ（既定の保持期間 7 日）
builder.Services.AddHostedService<MagicLinkCleanupService>();

// データベース初期化（シーダー）
builder.Services.AddScoped<IDbSeeder, OrganizationClientSeeder>();
builder.Services.AddScoped<IDbSeeder, AccountsOrganizationSeeder>();
builder.Services.AddScoped<IDbSeeder, B2BPasskeySeeder>();
builder.Services.AddScoped<DbInitializer>();
// Fido2.NetLib設定（動的RP IDに対応するためリクエストごとに設定を変更する必要あり）
// ここではデフォルト設定を登録し、実際のRP IDはB2BPasskeyService内で動的に処理
builder.Services.AddSingleton<IFido2>(sp =>
{
    // デフォルト設定（実際の使用時はクライアントごとのRP IDを使用）
    var config = new Fido2Configuration
    {
        ServerDomain = builder.Configuration["Fido2:ServerDomain"] ?? "localhost",
        ServerName = builder.Configuration["Fido2:ServerName"] ?? "EcAuth",
        Origins = new HashSet<string> { builder.Configuration["Fido2:Origin"] ?? "https://localhost" }
    };
    return new Fido2(config);
});
// Add services to the container.
builder.Services.AddDbContext<EcAuthDbContext>((sp, options) =>
{
    var tenantService = sp.GetRequiredService<ITenantService>();
    options.UseSqlServer(
        builder.Configuration["ConnectionStrings:EcAuthDbContext"],
        sqlOptions => sqlOptions.CommandTimeout(180) // タイムアウトを3分に設定
    );

    // EF Core 9のマイグレーション時の自動トランザクション管理警告を無視
    // 参照: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/breaking-changes
    // これにより、マイグレーション内でDbContextを作成する既存のパターンが動作します
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MigrationsUserTransactionWarning));
});
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddApiExplorer(options =>
{
    options.SubstituteApiVersionInUrl = true;
});
// Platform API（テナント横断）の CORS 設定
builder.Services.AddCors(options =>
{
    options.AddPolicy(PlatformApiConstants.CorsPolicy, policy =>
    {
        var allowedOrigins = NormalizeOrigins(
            builder.Configuration.GetSection("PlatformApi:AllowedOrigins").Get<string[]>());
        if (allowedOrigins.Length == 0)
        {
            throw new InvalidOperationException("PlatformApi:AllowedOrigins が appsettings に定義されていません。");
        }
        policy.WithOrigins(allowedOrigins)
              .SetIsOriginAllowedToAllowWildcardSubdomains()
              .AllowAnyHeader()
              .WithMethods("GET", "OPTIONS");
    });

    // Account 申込 API（/api/signup）の CORS 設定。
    // 申込フォーム／確認ページはフロントエンド（Cloudflare Pages）から配信され、API は別ホスト
    // （accounts.ec-auth.io / stg-accounts.ec-auth.io）に露出するためクロスオリジンになる。
    // 本番フロントは ec-auth.io / www.ec-auth.io。staging プレビュー Pages など追加のオリジンは
    // Signup:AllowedOrigins（本番 Terraform app_settings で注入）で上書きする。
    // ※ accounts / stg-accounts は B2B パスキー org 用テナント（= API のホスト）であって
    //    フロントの配信元ではない点に注意（フォールバックには含めない）。
    options.AddPolicy(IdentityProvider.Controllers.SignupController.CorsPolicy, policy =>
    {
        var allowedOrigins = NormalizeOrigins(
            builder.Configuration.GetSection("Signup:AllowedOrigins").Get<string[]>());
        if (allowedOrigins.Length == 0)
        {
            allowedOrigins = new[] { "https://ec-auth.io", "https://www.ec-auth.io" };
        }
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .WithMethods("GET", "POST", "OPTIONS");
    });
});
builder.Services.AddControllers();
builder.Services.AddMvc();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Application Insights / OpenTelemetry
// 接続文字列は環境変数 APPLICATIONINSIGHTS_CONNECTION_STRING 経由で供給される。
// SDK は接続文字列未設定時に起動失敗例外を投げるため、設定がある場合のみ有効化する。
// （ローカル dev / CI 等では未設定 → SDK 無効、Azure staging/production では設定あり → 有効）
var appInsightsConnectionString =
    Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
}

var app = builder.Build();

// データベース初期化（シーダー実行）
using (var scope = app.Services.CreateScope())
{
    var dbInitializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    var context = scope.ServiceProvider.GetRequiredService<EcAuthDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        // RUN_MIGRATIONS_ON_STARTUP=true の場合のみマイグレーションを自動適用する。
        // compose.yaml のみで設定しており、ローカル dev / CI / Azure の本番環境では適用されない。
        if (configuration.GetValue<bool>("RUN_MIGRATIONS_ON_STARTUP"))
        {
            logger.LogInformation("Applying database migrations on startup...");
            await context.Database.MigrateAsync();
        }

        await dbInitializer.InitializeAsync(context, configuration);
    }
    catch (Exception ex)
    {
        if (app.Environment.IsDevelopment())
        {
            // 開発環境ではシード失敗時に起動を停止（問題を即座に検知）
            logger.LogError(ex, "DbInitializer failed. Stopping application in Development environment.");
            throw;
        }
        else
        {
            // 本番環境ではシード失敗時も起動を継続（サービス可用性を優先）
            logger.LogError(ex, "DbInitializer failed. Continuing startup in Production environment.");
        }
    }
}

// 起動時シーダー（DbInitializer / OrganizationClientSeeder 等）のログ・トレースは app.Run() より前、
// すなわち OpenTelemetry のバッチ送信（BatchExportProcessor の遅延フラッシュ）が回り切る前の区間で
// 発生する。デプロイ起動時にプロセスが入れ替わると、これら起動診断ログ（シーダーの実行/スキップ/
// B2C→B2B 補正/失敗）がフラッシュ前に失われ、本番 App Insights に出ない。
// （Issue #415: 起動ログが本番テレメトリに現れず、シーダーが「実行されたが補正しなかった」のか
//  「そもそも実行されていない」のかを切り分けられなかった問題への対応）
// シード完了直後に明示フラッシュして、起動診断ログを確実にエクスポートする。
// 接続文字列がある場合のみ OpenTelemetry を有効化しているため、同条件でのみプロバイダを解決する。
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    app.Services.GetService<LoggerProvider>()?.ForceFlush();
    app.Services.GetService<TracerProvider>()?.ForceFlush();
}

// Configure the HTTP request pipeline.

// リバースプロキシ（Cloudflare）からの実クライアント IP 復元。RemoteIpAddress を参照する
// 後続のミドルウェア・OpenTelemetry instrumentation（Application Insights の client_IP）より
// 前に置く必要があるため、パイプライン先頭で実行する。
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 静的ファイルの配信（B2Bパスキーテストページ用）
// 本番環境では DEFAULT_ORGANIZATION_TENANT_NAME のサブドメインからのリクエストのみ許可
if (app.Environment.IsProduction())
{
    var allowedTenant = Environment.GetEnvironmentVariable("DEFAULT_ORGANIZATION_TENANT_NAME") ?? "";
    app.UseWhen(
        context =>
        {
            var host = context.Request.Host.Host;
            var segments = host.Split('.');
            return segments.Length > 2 && segments[0].Equals(allowedTenant, StringComparison.OrdinalIgnoreCase);
        },
        appBuilder => appBuilder.UseStaticFiles()
    );
}
else
{
    app.UseStaticFiles();
}

// Platform API の CORS ミドルウェア（/platform/ パスのみ適用）
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(PlatformApiConstants.PathPrefix),
    appBuilder => appBuilder.UseCors(PlatformApiConstants.CorsPolicy)
);

// Account 申込 API（/api/signup）の CORS。Controller の [EnableCors] 属性で
// SignupController.CorsPolicy を適用するため、パイプラインに CORS ミドルウェアを配置する。
app.UseCors();

app.UseMiddleware<TenantMiddleware>();

// 開発環境ではHTTPSリダイレクトを無効化（E2Eテストのため）
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

// ヘルスチェックエンドポイント
app.MapGet("/healthz", async (EcAuthDbContext dbContext) =>
{
    try
    {
        // データベース接続確認
        await dbContext.Database.CanConnectAsync();
        return Results.Ok(new { status = "healthy", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { status = "unhealthy", database = "disconnected", error = ex.Message },
            statusCode: 503
        );
    }
});

app.Run();

// CORS の WithOrigins は origin を完全一致（scheme + host + port）で比較するため、
// 末尾スラッシュ付き・前後空白・空値が混入するとマッチしない。設定ミス時の原因切り分けが
// 難しくなるのを避けるため、両 CORS ポリシーのフォールバック値を共通でサニタイズする。
// （正規化後に空かどうかの扱いは呼び出し側で判断する: PlatformApi は例外、Signup は既定値）
static string[] NormalizeOrigins(string[]? origins) =>
    (origins ?? Array.Empty<string>())
        .Select(o => o?.Trim().TrimEnd('/'))
        .Where(o => !string.IsNullOrWhiteSpace(o))
        .Select(o => o!)
        .Distinct()
        .ToArray();
