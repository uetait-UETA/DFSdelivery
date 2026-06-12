using Dapper;
using CreaFacturasVentasSAP.Data;
using CreaFacturasVentasSAP.Models;

namespace CreaFacturasVentasSAP.Repositories;

public interface IInvoiceSalesRepository
{
    /// <summary>
    /// Retorna todos los ítems de ventas/devoluciones con ODLN/ORDN creado (DocNum > 0)
    /// para las fechas en el rango [desde, hasta] que aún no tienen factura diaria.
    /// Se unen con la_daily_invoices para saber si ya fueron facturados.
    /// </summary>
    Task<IEnumerable<InvoiceSalesItem>> GetItemsParaFacturarAsync(DateTime desde, DateTime hasta);

    /// <summary>
    /// Verifica si hay ítems con DeliveryDocNum IS NULL o -1 para una tienda/fecha/tipo.
    /// </summary>
    Task<bool> HayEntregasPendientesAsync(string cardCode, DateTime fecha, string transType);

    /// <summary>
    /// Verifica si hay filas en la_delivery_errors para una tienda/fecha.
    /// </summary>
    Task<bool> HayErroresPendientesAsync(string cardCode, DateTime fecha);
}

public class InvoiceSalesRepository : IInvoiceSalesRepository
{
    private readonly IDbConnectionFactory _factory;

    public InvoiceSalesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IEnumerable<InvoiceSalesItem>> GetItemsParaFacturarAsync(
        DateTime desde, DateTime hasta)
    {
        const string sql = """
            SELECT
                ss.ID,
                ss.CompanyId,
                ss.transnum          AS Transnum,
                ss.itemnum           AS Itemnum,
                ss.storenum          AS Storenum,
                ss.NUMSERIE          AS Numserie,
                ss.itemdatetime      AS Itemdatetime,
                ts.CARCODE           AS CardCode,
                ts.CompanyId         AS BPLId,
                ts.WHSCODE           AS WhsCode,
                ss.TRANSTYPE         AS TransType,
                ss.DeliveryDocNum,
                ss.DeliveryDocEntry,
                ss.txnmodifier       AS Txnmodifier
            FROM [dbo].[la_store_sales] ss
            INNER JOIN [dbo].[ADR_TIENDA_SERIE] ts
                ON ts.NUMSERIE = ss.NUMSERIE
               AND ts.NUMSTORE = CAST(ss.storenum AS INT)
               AND ts.DUTYTYPE = ss.TRANSTYPE
            WHERE ss.DeliveryDocNum > 0
              AND ss.DeliveryDocEntry IS NOT NULL
              AND CAST(ss.itemdatetime AS DATE) BETWEEN @Desde AND @Hasta
              AND NOT EXISTS (
                  SELECT 1 FROM [dbo].[la_daily_invoices] di
                  WHERE di.CardCode  = ts.CARCODE
                    AND di.FechaDoc  = CAST(ss.itemdatetime AS DATE)
                    AND di.TransType = ss.TRANSTYPE
                    AND di.InvoiceDocNum > 0
              )
            ORDER BY ss.itemdatetime, ss.transnum, ss.itemnum
            """;

        await using var conn = _factory.CreateInternal();
        return await conn.QueryAsync<InvoiceSalesItem>(sql, new { Desde = desde.Date, Hasta = hasta.Date });
    }

    public async Task<bool> HayEntregasPendientesAsync(
        string cardCode, DateTime fecha, string transType)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM [dbo].[la_store_sales] ss
                INNER JOIN [dbo].[ADR_TIENDA_SERIE] ts
                    ON ts.NUMSERIE = ss.NUMSERIE
                   AND ts.NUMSTORE = CAST(ss.storenum AS INT)
                   AND ts.DUTYTYPE = ss.TRANSTYPE
                WHERE ts.CARCODE            = @CardCode
                  AND CAST(ss.itemdatetime AS DATE) = @Fecha
                  AND ss.TRANSTYPE          = @TransType
                  AND (ss.DeliveryDocNum IS NULL OR ss.DeliveryDocNum = -1)
            ) THEN 1 ELSE 0 END
            """;

        await using var conn = _factory.CreateInternal();
        return await conn.ExecuteScalarAsync<int>(sql,
            new { CardCode = cardCode, Fecha = fecha.Date, TransType = transType }) == 1;
    }

    public async Task<bool> HayErroresPendientesAsync(string cardCode, DateTime fecha)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM [dbo].[la_delivery_errors] de
                INNER JOIN [dbo].[la_store_sales] ss
                    ON ss.transnum = de.transnum AND ss.itemnum = de.itemnum
                INNER JOIN [dbo].[ADR_TIENDA_SERIE] ts
                    ON ts.NUMSERIE = ss.NUMSERIE
                   AND ts.NUMSTORE = CAST(ss.storenum AS INT)
                   AND ts.DUTYTYPE = ss.TRANSTYPE
                WHERE ts.CARCODE = @CardCode
                  AND CAST(ss.itemdatetime AS DATE) = @Fecha
            ) THEN 1 ELSE 0 END
            """;

        await using var conn = _factory.CreateInternal();
        return await conn.ExecuteScalarAsync<int>(sql,
            new { CardCode = cardCode, Fecha = fecha.Date }) == 1;
    }
}
