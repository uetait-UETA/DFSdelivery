using ExtraeCreaDocSalesSAP.Repositories;
using Microsoft.Extensions.Logging;

namespace ExtraeCreaDocSalesSAP.Services;

public interface IBackfillService
{
    Task<int> RunAsync(CancellationToken ct = default);
    Task<int> RunTicketNoAsync(CancellationToken ct = default);
}

public class BackfillService : IBackfillService
{
    private readonly IStoreSalesRepository _salesRepo;
    private readonly IPosRepository _posRepo;
    private readonly ISapService _sap;
    private readonly ILogger<BackfillService> _logger;

    public BackfillService(
        IStoreSalesRepository salesRepo,
        IPosRepository posRepo,
        ISapService sap,
        ILogger<BackfillService> logger)
    {
        _salesRepo = salesRepo;
        _posRepo   = posRepo;
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

    public async Task<int> RunTicketNoAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("BACKFILL TICKETNO: actualizando TicketNo en la_store_sales...");

        var sinTicket = (await _salesRepo.GetSalesWithoutTicketNoAsync()).ToList();
        _logger.LogInformation("Registros sin TicketNo: {Count}", sinTicket.Count);

        if (sinTicket.Count == 0)
        {
            _logger.LogInformation("Nada que actualizar.");
            return 0;
        }

        _logger.LogInformation("Consultando lookup de TicketNo en POS...");
        var lookup = await _posRepo.GetTicketNoLookupAsync();
        _logger.LogInformation("Combinaciones en la vista POS: {Count}", lookup.Count);

        int actualizados = 0;
        int sinMatch     = 0;

        foreach (var (id, numserie, numalbaran) in sinTicket)
        {
            if (ct.IsCancellationRequested) break;

            var key = $"{numserie.Trim()}|{numalbaran}";

            if (!lookup.TryGetValue(key, out var ticketNo))
            {
                sinMatch++;
                continue;
            }

            await _salesRepo.UpdateTicketNoAsync(id, ticketNo);
            actualizados++;
        }

        _logger.LogInformation(
            "Backfill TicketNo finalizado — Actualizados: {A} | Sin match en POS: {S}",
            actualizados, sinMatch);
        _logger.LogInformation("═══════════════════════════════════════════════════════");

        return actualizados;
    }
}
