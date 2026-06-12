using Dapper;
using CreaFacturasVentasSAP.Data;
using CreaFacturasVentasSAP.Models;

namespace CreaFacturasVentasSAP.Repositories;

public interface IPaymentRepository
{
    /// <summary>
    /// Retorna los transnum que ya están en la_store_payments para evitar duplicados.
    /// </summary>
    Task<HashSet<string>> GetExistingTransnumsAsync();

    /// <summary>
    /// Inserta pagos nuevos desde el POS en la_store_payments.
    /// </summary>
    Task InsertManyAsync(IEnumerable<StoreTender> tenders);

    /// <summary>
    /// Retorna todos los pagos (no cambio/vuelta) para un grupo de transnum en una fecha.
    /// </summary>
    Task<IEnumerable<StoreTender>> GetPagosParaFacturaAsync(
        string cardCode, DateTime fecha, string transType);

    /// <summary>
    /// Retorna todos los mapeos TenderID → SAP de ADR_TENDER_SAP.
    /// </summary>
    Task<Dictionary<string, TenderSapMapping>> GetTenderMappingsAsync();
}

public class PaymentRepository : IPaymentRepository
{
    private readonly IDbConnectionFactory _factory;

    public PaymentRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<HashSet<string>> GetExistingTransnumsAsync()
    {
        const string sql = "SELECT DISTINCT transnum FROM [dbo].[la_store_payments]";
        await using var conn = _factory.CreateInternal();
        var result = await conn.QueryAsync<string>(sql);
        return result.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InsertManyAsync(IEnumerable<StoreTender> tenders)
    {
        const string sql = """
            INSERT INTO [dbo].[la_store_payments]
                (CompanyId, transnum, TransactionID, TenderID, Amount, CurrencyID,
                 LineType, IsChange, SiteID, CashDrawerID, BusinessDayDate)
            VALUES
                (@CompanyId, @Transnum, @TransactionID, @TenderID, @Amount, @CurrencyID,
                 @LineType, @IsChange, @SiteID, @CashDrawerID, @BusinessDayDate)
            """;

        await using var conn = _factory.CreateInternal();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync(sql, tenders, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<IEnumerable<StoreTender>> GetPagosParaFacturaAsync(
        string cardCode, DateTime fecha, string transType)
    {
        const string sql = """
            SELECT DISTINCT
                sp.ID, sp.CompanyId, sp.transnum AS Transnum, sp.TransactionID,
                sp.TenderID, sp.Amount, sp.CurrencyID, sp.LineType,
                sp.IsChange, sp.SiteID, sp.CashDrawerID, sp.BusinessDayDate
            FROM [dbo].[la_store_payments] sp
            INNER JOIN [dbo].[la_store_sales] ss ON ss.transnum = sp.transnum
            INNER JOIN [dbo].[ADR_TIENDA_SERIE] ts
                ON ts.NUMSERIE = ss.NUMSERIE
               AND ts.NUMSTORE = CAST(ss.storenum AS INT)
               AND ts.DUTYTYPE = ss.TRANSTYPE
            WHERE ts.CARCODE            = @CardCode
              AND sp.BusinessDayDate    = @Fecha
              AND ss.TRANSTYPE          = @TransType
              AND sp.IsChange           = 0
              AND sp.LineType           = 'TenderMerch'
            """;

        await using var conn = _factory.CreateInternal();
        return await conn.QueryAsync<StoreTender>(sql,
            new { CardCode = cardCode, Fecha = fecha.Date, TransType = transType });
    }

    public async Task<Dictionary<string, TenderSapMapping>> GetTenderMappingsAsync()
    {
        const string sql = """
            SELECT TenderID, SapAccount, PaymentType, Description
            FROM [dbo].[ADR_TENDER_SAP]
            """;

        await using var conn = _factory.CreateInternal();
        var rows = await conn.QueryAsync<TenderSapMapping>(sql);
        return rows.ToDictionary(r => r.TenderID, StringComparer.OrdinalIgnoreCase);
    }
}
