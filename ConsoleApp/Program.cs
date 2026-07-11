using ConsoleAppFramework;
using IdentityProvider.Models;
using IdentityProvider.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

using var host = Host.CreateDefaultBuilder()
    .ConfigureServices((hostContext, services) =>
    {
        services.AddDbContext<EcAuthDbContext>(options =>
            options.UseSqlServer(configuration["ConnectionStrings:EcAuthDbContext"]));

        // EcAuthDbContext のコンストラクタが ITenantService を要求する。Client エンティティ自体は
        // テナントフィルタを持たないため、テナント未設定（TenantName = null）でも全クライアントを列挙できる。
        services.AddSingleton<ITenantService, TenantService>();

        // client_secret の保存時暗号化（EcAuthDocs#106）。backfill は「平文 → kv1: 暗号化」が目的のため、
        // アプリ本体の平文フォールバック（保険）とは異なり Key Vault 必須とする。鍵 URI 未設定なら
        // AddSecretProtection が InvalidOperationException を投げ、平文で no-op 実行される事故を防ぐ。
        services.AddSecretProtection(options =>
        {
            options.UsePlaintext = false;
            options.KeyVaultKeyId = configuration["ClientSecretProtection:KeyVaultKeyId"];
        });

        services.AddLogging(builder => builder.AddSimpleConsole());
    }).Build();

ConsoleApp.ServiceProvider = host.Services;
var app = ConsoleApp.Create();

await app.RunAsync(args);
