using Microsoft.EntityFrameworkCore;
using MockOpenIdProvider.Models;
using MockOpenIdProvider.Services;
using MockOpenIdProvider.Middlewares;
using System.Configuration;

var builder = WebApplication.CreateBuilder(args);

// OrganizationService をScoped サービスとして登録
builder.Services.AddScoped<IOrganizationService, OrganizationService>();

// Add services to the container.
builder.Services.AddDbContext<IdpDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration["ConnectionStrings:MockIdpDbContext"],
        sqlOptions => sqlOptions.CommandTimeout(180) // タイムアウトを3分に設定
    );

    // EF Core 9のマイグレーション時の自動トランザクション管理警告を無視
    // 参照: https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-9.0/breaking-changes
    // これにより、マイグレーション内でDbContextを作成する既存のパターンが動作します
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MigrationsUserTransactionWarning));
});
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddOptions<MockIdP>().BindConfiguration(nameof(MockIdP)).ValidateDataAnnotations();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();

// OrganizationMiddleware を認証・認可の前に配置
app.UseMiddleware<OrganizationMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
