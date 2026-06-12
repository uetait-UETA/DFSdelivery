using ExtraeCreaDocSalesSAP.Configuration;
using ExtraeCreaDocSalesSAP.Data;
using ExtraeCreaDocSalesSAP.Repositories;
using ExtraeCreaDocSalesSAP.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// ── Bootstrap ──────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Services.AddSerilog();

// Settings tipados
builder.Services.Configure<AppSettings>(cfg =>
{
    builder.Configuration.GetSection("SapServiceLayer").Bind(cfg.SapServiceLayer);
    builder.Configuration.GetSection("ProcessingOptions").Bind(cfg.ProcessingOptions);
    builder.Configuration.GetSection("FreightSettings").Bind(cfg.FreightSettings);
});

// Infraestructura
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();

// SAP HttpClient — una sola empresa, un solo cliente HTTP
var sapCfg = builder.Configuration.GetSection("SapServiceLayer").Get<SapServiceLayerSettings>()
    ?? new SapServiceLayerSettings();

builder.Services.AddHttpClient<ISapService, SapService>(client =>
{
    client.BaseAddress = new Uri(sapCfg.BaseUrl.TrimEnd('/') + "/");
    client.Timeout     = TimeSpan.FromSeconds(sapCfg.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = sapCfg.IgnoreSslErrors
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null,
    UseCookies = false // cookies gestionadas manualmente (sesión B1SESSION)
});

// Repositorios y servicios
builder.Services.AddScoped<IPosRepository, PosRepository>();
builder.Services.AddScoped<IStoreSalesRepository, StoreSalesRepository>();
builder.Services.AddScoped<ITiendaSerieRepository, TiendaSerieRepository>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddScoped<IBackfillService, BackfillService>();

var host = builder.Build();

// ── Ejecución ─────────────────────────────────────────────────────────────────

int exitCode = 0;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    bool modoBackfill = args.Contains("--backfill", StringComparer.OrdinalIgnoreCase);

    Log.Information("ExtraeCreaDocSalesSAP iniciado. Entorno: {Env} | Modo: {Modo}",
        builder.Environment.EnvironmentName,
        modoBackfill ? "BACKFILL" : "SYNC");

    await using var scope = host.Services.CreateAsyncScope();

    if (modoBackfill)
    {
        var backfill = scope.ServiceProvider.GetRequiredService<IBackfillService>();
        await backfill.RunAsync(cts.Token);
    }
    else
    {
        var sync = scope.ServiceProvider.GetRequiredService<ISyncService>();
        await sync.RunAsync(cts.Token);
    }
}
catch (OperationCanceledException)
{
    Log.Warning("Proceso cancelado por el usuario.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Error crítico. El proceso terminó inesperadamente.");
    exitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

return exitCode;
