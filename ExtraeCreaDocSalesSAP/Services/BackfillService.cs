using ExtraeCreaDocSalesSAP.Repositories;
using Microsoft.Extensions.Logging;

namespace ExtraeCreaDocSalesSAP.Services;

public interface IBackfillService
{
    Task<int> RunAsync(CancellationToken ct = default);
}

public class BackfillService : IBackfillService
{
    private readonly IStoreSalesRepository _salesRepo;
    private readonly ISapService _sap;
    private readonly ILogger<BackfillService> _logger;

    public BackfillService(
        IStoreSalesRepository salesRepo,
        ISapService sap,
        ILogger<BackfillService> logger)
    {
        _salesRepo = salesRepo;
        _sap       = sap;
        _logger    = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("BACKFILL: actualizando DeliveryDocEntry desde SAP B1...");

        var pendientes = (await _salesRepo.GetDocNumsSinEntryAsync()).ToList();

        _logger.LogInformation("DocNums sin DeliveryDocEntry: {Count}", pendientes.Count);

        if (pendientes.Count == 0)
        {
            _logger.LogInformation("Nada que actualizar.");
            return 0;
        }

        bool loggedIn = await _sap.LoginAsync();
        if (!loggedIn)
        {
            _logger.LogError("No se pudo autenticar en SAP B1. Backfill cancelado.");
            return 0;
        }

        int actualizados = 0;
        int noEncontrados = 0;

        try
        {
            foreach (var (docNum, esDevolucion) in pendientes)
            {
                if (ct.IsCancellationRequested) break;

                var tipo = esDevolucion ? "ORDN" : "ODLN";

                int? docEntry = await _sap.GetDocEntryByDocNumAsync((int)docNum, esDevolucion);

                if (docEntry is null)
                {
                    _logger.LogWarning(
                        "DocNum={N} ({Tipo}) no encontrado en SAP. Se omite.",
                        docNum, tipo);
                    noEncontrados++;
                    continue;
                }

                await _salesRepo.UpdateDocEntryByDocNumAsync(docNum, docEntry.Value);

                _logger.LogInformation(
                    "Actualizado: {Tipo} DocNum={N} → DocEntry={E}",
                    tipo, docNum, docEntry.Value);

                actualizados++;
            }
        }
        finally
        {
            await _sap.LogoutAsync();
        }

        _logger.LogInformation(
            "Backfill finalizado — Actualizados: {A} | No encontrados en SAP: {NE}",
            actualizados, noEncontrados);
        _logger.LogInformation("═══════════════════════════════════════════════════════");

        return actualizados;
    }
}
