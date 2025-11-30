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
