using Dapper;
using ExtraeCreaDocSalesSAP.Data;
using ExtraeCreaDocSalesSAP.Models;
using Microsoft.Extensions.Logging;

namespace ExtraeCreaDocSalesSAP.Repositories;

public interface IStoreSalesRepository
{
    Task<HashSet<string>> GetExistingKeysAsync();
    Task InsertManyAsync(IEnumerable<StoreSale> sales);
    Task<IEnumerable<StoreSale>> GetPendingAsync();
    Task UpdateDeliveryDocNumAsync(long id, long docNum, long docEntry);
    Task MarkAsErrorAsync(long id);
    Task UpsertErrorAsync(DeliveryError error);
    Task DeleteErrorAsync(string transnum, int itemnum);

    /// <summary>
    /// Retorna los DocNum distintos que ya tienen ODLN/ORDN creado
    /// pero todavía no tienen DeliveryDocEntry guardado.
    /// </summary>
    Task<IEnumerable<(long DocNum, bool EsDevolucion)>> GetDocNumsSinEntryAsync();

    /// <summary>Actualiza DeliveryDocEntry para todos los ítems con ese DocNum.</summary>
    Task UpdateDocEntryByDocNumAsync(long docNum, long docEntry);
}

public class StoreSalesRepository : IStoreSalesRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<StoreSalesRepository> _logger;

    public StoreSalesRepository(IDbConnectionFactory factory, ILogger<StoreSalesRepository> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public async Task<HashSet<string>> GetExistingKeysAsync()
    {
        const string sql =
            "SELECT transnum + '|' + CAST(itemnum AS VARCHAR) FROM [dbo].[la_store_sales]";

        await using var conn = _factory.CreateInternal();
        var keys = await conn.QueryAsync<string>(sql);
        return keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InsertManyAsync(IEnumerable<StoreSale> sales)
    {
        const string sql = """
            INSERT INTO [dbo].[la_store_sales]
                (CompanyId, transnum, itemnum, txnvoidmod, txnmodifier,
                 storenum, itemdatetime, skunum, pludesc, qty,
                 stdunitprice, extsellprice, extundiscprice, DeliveryDocNum,
                 NUMSERIE, NUMALBARAN, ABONODE_NUMALBARAN, ABONODE_NUMSERIE,
                 TRANSTYPE, CodigoImpuesto, DutyType, SalesTaxAmount)
            VALUES
                (@CompanyId, @Transnum, @Itemnum, @Txnvoidmod, @Txnmodifier,
                 @Storenum, @Itemdatetime, @Skunum, @Pludesc, @Qty,
                 @Stdunitprice, @Extsellprice, @Extundiscprice, NULL,
                 @Numserie, @Numalbaran, @Abonode_Numalbaran, @Abonode_Numserie,
                 @TransType, @CodigoImpuesto, @DutyType, @SalesTaxAmount)
            """;

        await using var conn = _factory.CreateInternal();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync(sql, sales, tx);
            await tx.CommitAsync();
            _logger.LogDebug("Insertados {Count} registros en la_store_sales", sales.Count());
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<IEnumerable<StoreSale>> GetPendingAsync()
    {
        const string sql = """
            SELECT
                ID,
                CompanyId,
                transnum            AS Transnum,
                itemnum             AS Itemnum,
                txnvoidmod          AS Txnvoidmod,
                txnmodifier         AS Txnmodifier,
                storenum            AS Storenum,
                itemdatetime        AS Itemdatetime,
                skunum              AS Skunum,
                pludesc             AS Pludesc,
                qty                 AS Qty,
                stdunitprice        AS Stdunitprice,
                extsellprice        AS Extsellprice,
                extundiscprice      AS Extundiscprice,
                DeliveryDocNum,
                NUMSERIE            AS Numserie,
                NUMALBARAN          AS Numalbaran,
                ABONODE_NUMALBARAN  AS Abonode_Numalbaran,
                ABONODE_NUMSERIE    AS Abonode_Numserie,
                ISNULL(TRANSTYPE, 'DF')        AS TransType,
                ISNULL(CodigoImpuesto, '')     AS CodigoImpuesto,
                ISNULL(DutyType, 'DF')         AS DutyType,
                ISNULL(SalesTaxAmount, 0)      AS SalesTaxAmount
            FROM [dbo].[la_store_sales]
            WHERE DeliveryDocNum IS NULL OR DeliveryDocNum = -1
            ORDER BY Itemdatetime, Transnum, Itemnum
            """;

        await using var conn = _factory.CreateInternal();
        return await conn.QueryAsync<StoreSale>(sql);
    }

    public async Task UpdateDeliveryDocNumAsync(long id, long docNum, long docEntry)
    {
        const string sql =
            "UPDATE [dbo].[la_store_sales] SET DeliveryDocNum = @DocNum, DeliveryDocEntry = @DocEntry WHERE ID = @Id";
        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new { Id = id, DocNum = docNum, DocEntry = docEntry });
    }

    public async Task MarkAsErrorAsync(long id)
    {
        const string sql =
            "UPDATE [dbo].[la_store_sales] SET DeliveryDocNum = -1 WHERE ID = @Id";
        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new { Id = id });
    }

    public async Task UpsertErrorAsync(DeliveryError error)
    {
        const string sql = """
            MERGE [dbo].[la_delivery_errors] AS target
            USING (SELECT @Transnum AS transnum, @Itemnum AS itemnum) AS source
                ON target.transnum = source.transnum AND target.itemnum = source.itemnum
            WHEN MATCHED THEN
                UPDATE SET
                    error_message  = @ErrorMessage,
                    date_created   = @DateCreated,
                    skunum         = @Skunum,
                    pludesc        = @Pludesc,
                    qty            = @Qty,
                    extsellprice   = @Extsellprice,
                    extundiscprice = @Extundiscprice
            WHEN NOT MATCHED THEN
                INSERT (CompanyId, transnum, itemnum, txnvoidmod, txnmodifier,
                        storenum, itemdatetime, skunum, pludesc, qty,
                        stdunitprice, extsellprice, extundiscprice, DeliveryDocNum,
                        date_created, error_message)
                VALUES (@CompanyId, @Transnum, @Itemnum, @Txnvoidmod, @Txnmodifier,
                        @Storenum, @Itemdatetime, @Skunum, @Pludesc, @Qty,
                        @Stdunitprice, @Extsellprice, @Extundiscprice, @DeliveryDocNum,
                        @DateCreated, @ErrorMessage);
            """;

        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, error);
    }

    public async Task DeleteErrorAsync(string transnum, int itemnum)
    {
        const string sql =
            "DELETE FROM [dbo].[la_delivery_errors] WHERE transnum = @Transnum AND itemnum = @Itemnum";
        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new { Transnum = transnum, Itemnum = itemnum });
    }

    public async Task<IEnumerable<(long DocNum, bool EsDevolucion)>> GetDocNumsSinEntryAsync()
    {
        const string sql = """
            SELECT DISTINCT
                DeliveryDocNum          AS DocNum,
                MAX(txnmodifier)        AS Modifier
            FROM [dbo].[la_store_sales]
            WHERE DeliveryDocNum > 0
              AND DeliveryDocEntry IS NULL
            GROUP BY DeliveryDocNum
            ORDER BY DeliveryDocNum
            """;

        await using var conn = _factory.CreateInternal();
        var rows = await conn.QueryAsync<(long DocNum, int Modifier)>(sql);
        return rows.Select(r => (r.DocNum, r.Modifier == 1));
    }

    public async Task UpdateDocEntryByDocNumAsync(long docNum, long docEntry)
    {
        const string sql =
            "UPDATE [dbo].[la_store_sales] SET DeliveryDocEntry = @DocEntry WHERE DeliveryDocNum = @DocNum";
        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new { DocNum = docNum, DocEntry = docEntry });
    }
}
