using System.Net.Http.Headers;
using CreaFacturasVentasSAP.Configuration;
using CreaFacturasVentasSAP.Data;
using CreaFacturasVentasSAP.Repositories;
using CreaFacturasVentasSAP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<AppSettings>(ctx.Configuration.GetSection("CreaFacturas"));

        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();

        services.AddHttpClient<ISapInvoiceService, SapInvoiceService>((sp, client) =>
        {
            var cfg = sp.GetRequiredService<IOptions<AppSettings>>().Value.SapServiceLayer;
            client.BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/");
            client.Timeout     = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
            client.DefaultRequestHeaders.Accept
                .Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<AppSettings>>().Value.SapServiceLayer;
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    cfg.IgnoreSslErrors ? (_, _, _, _) => true : null
            };
        });

        services.AddScoped<IInvoiceSalesRepository, InvoiceSalesRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IDailyInvoiceRepository, DailyInvoiceRepository>();
        services.AddScoped<IInvoiceOrchestrator, InvoiceOrchestrator>();
    })
    .Build();

await using var scope = host.Services.CreateAsyncScope();
var orchestrator = scope.ServiceProvider.GetRequiredService<IInvoiceOrchestrator>();

bool paymentsOnly = args.Contains("--payments-only", StringComparer.OrdinalIgnoreCase);

try
{
    if (paymentsOnly)
        await orchestrator.RunPaymentsOnlyAsync();
    else
        await orchestrator.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Error fatal en CreaFacturasVentasSAP");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
