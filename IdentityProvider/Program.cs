using Fido2NetLib;
using IdentityProvider.Filters;
using IdentityProvider.Middlewares;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IUserService, UserService>();
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
builder.Services.AddControllers();
builder.Services.AddMvc();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // 開発環境でのみ静的ファイルを有効化（B2Bパスキーテストページ用）
    app.UseStaticFiles();
}

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
