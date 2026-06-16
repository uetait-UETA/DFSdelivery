using Dapper;
using ExtraeCreaDocSalesSAP.Data;
using ExtraeCreaDocSalesSAP.Models;
using Microsoft.Extensions.Logging;

namespace ExtraeCreaDocSalesSAP.Repositories;

public interface IPosRepository
{
    Task<IEnumerable<PosVentaItem>> GetVentasAsync(string filterViewType, int batchSize);

    /// <summary>
    /// Retorna un diccionario "CashDrawerID|SecuenciaTransaccion" → "SiteID-CashDrawerID-NroTicket"
    /// para poder actualizar TicketNo en registros existentes.
    /// </summary>
    Task<Dictionary<string, string>> GetTicketNoLookupAsync();
}

public class PosRepository : IPosRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<PosRepository> _logger;

    public PosRepository(IDbConnectionFactory factory, ILogger<PosRepository> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public async Task<Dictionary<string, string>> GetTicketNoLookupAsync()
    {
        const string sql = """
            SELECT DISTINCT
                CashDrawerID,
                SecuenciaTransaccion,
                SiteID,
                NroTicket
            FROM [dbo].[vw_POS_VentaItems]
            """;

        await using var conn = _factory.CreateExternal();
        var rows = await conn.QueryAsync<(string CashDrawerID, long SecuenciaTransaccion, string SiteID, int NroTicket)>(sql);

        return rows.ToDictionary(
            r => $"{r.CashDrawerID}|{r.SecuenciaTransaccion}",
            r => $"{r.SiteID}{r.CashDrawerID}{r.NroTicket}",
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<PosVentaItem>> GetVentasAsync(string filterViewType, int batchSize)
    {
        const string sql = """
            SELECT TOP (@BatchSize)
                TransactionID,
                SiteID,
                PosLoc,
                CashDrawerID,
                SecuenciaTransaccion,
                TransactionViewType,
                TransType,
                BusinessDayDate,
                ItemLineID,
                SaleLineNumber,
                LineNumber,
                ItemAccion,
                POSItemID,
                Descripcion,
                DutyType,
                InventoryLocation,
                Cantidad,
                PrecioUnitario,
                MontoExtendido,
                MontoSinImpuesto,
                TransactionGrandAmount,
                TransactionNetAmount,
                CodigoImpuesto,
                DevWorkstation,
                DevNroTicket,
                NroTicket
            FROM [dbo].[vw_POS_VentaItems]
            WHERE TransactionViewType = @FilterViewType
              AND ItemAccion          IN ('SELL', 'RETURN')
              AND POSItemID IS NOT NULL
              AND POSItemID <> ''
              AND PosLoc    IS NOT NULL
              AND PosLoc    <> ''
            ORDER BY BusinessDayDate, TransactionID, SaleLineNumber
            """;

        await using var conn = _factory.CreateExternal();
        _logger.LogDebug("Consultando vw_POS_VentaItems (filtro: {ViewType}, lote: {BatchSize})",
            filterViewType, batchSize);

        return await conn.QueryAsync<PosVentaItem>(sql,
            new { FilterViewType = filterViewType, BatchSize = batchSize });
    }
}
