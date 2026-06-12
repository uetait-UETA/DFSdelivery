using Dapper;
using CreaFacturasVentasSAP.Data;
using CreaFacturasVentasSAP.Models;

namespace CreaFacturasVentasSAP.Repositories;

public interface IDailyInvoiceRepository
{
    /// <summary>Inserta o actualiza el registro de factura (upsert por CardCode+FechaDoc+TransType).</summary>
    Task UpsertAsync(DailyInvoice invoice);

    /// <summary>Marca la factura como error con -1 y guarda el mensaje.</summary>
    Task MarkAsErrorAsync(string cardCode, DateTime fecha, string transType, string error);

    /// <summary>Actualiza el DocNum y DocEntry del Incoming Payment una vez creado.</summary>
    Task SetPaymentAsync(string cardCode, DateTime fecha, string transType, long paymentDocNum);
}

public class DailyInvoiceRepository : IDailyInvoiceRepository
{
    private readonly IDbConnectionFactory _factory;

    public DailyInvoiceRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(DailyInvoice inv)
    {
        const string sql = """
            MERGE [dbo].[la_daily_invoices] AS target
            USING (SELECT @CardCode AS CardCode, @FechaDoc AS FechaDoc, @TransType AS TransType) AS source
                ON target.CardCode  = source.CardCode
               AND target.FechaDoc  = source.FechaDoc
               AND target.TransType = source.TransType
            WHEN MATCHED THEN
                UPDATE SET
                    InvoiceDocNum   = @InvoiceDocNum,
                    InvoiceDocEntry = @InvoiceDocEntry,
                    error_message   = @ErrorMessage,
                    date_updated    = GETDATE()
            WHEN NOT MATCHED THEN
                INSERT (CompanyId, CardCode, BPLId, FechaDoc, TransType,
                        InvoiceDocNum, InvoiceDocEntry, date_created, error_message)
                VALUES (@CompanyId, @CardCode, @BPLId, @FechaDoc, @TransType,
                        @InvoiceDocNum, @InvoiceDocEntry, GETDATE(), @ErrorMessage);
            """;

        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new
        {
            inv.CompanyId,
            inv.CardCode,
            inv.BPLId,
            FechaDoc        = inv.FechaDoc.Date,
            inv.TransType,
            inv.InvoiceDocNum,
            inv.InvoiceDocEntry,
            ErrorMessage    = inv.ErrorMessage
        });
    }

    public async Task MarkAsErrorAsync(
        string cardCode, DateTime fecha, string transType, string error)
    {
        const string sql = """
            MERGE [dbo].[la_daily_invoices] AS target
            USING (SELECT @CardCode AS CardCode, @FechaDoc AS FechaDoc, @TransType AS TransType) AS source
                ON target.CardCode  = source.CardCode
               AND target.FechaDoc  = source.FechaDoc
               AND target.TransType = source.TransType
            WHEN MATCHED THEN
                UPDATE SET InvoiceDocNum = -1, error_message = @Error, date_updated = GETDATE()
            WHEN NOT MATCHED THEN
                INSERT (CompanyId, CardCode, BPLId, FechaDoc, TransType,
                        InvoiceDocNum, date_created, error_message)
                VALUES (0, @CardCode, 0, @FechaDoc, @TransType, -1, GETDATE(), @Error);
            """;

        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new
        {
            CardCode = cardCode,
            FechaDoc = fecha.Date,
            TransType = transType,
            Error = error
        });
    }

    public async Task SetPaymentAsync(
        string cardCode, DateTime fecha, string transType, long paymentDocNum)
    {
        const string sql = """
            UPDATE [dbo].[la_daily_invoices]
            SET PaymentDocNum = @PaymentDocNum,
                date_updated  = GETDATE()
            WHERE CardCode  = @CardCode
              AND FechaDoc  = @FechaDoc
              AND TransType = @TransType
            """;

        await using var conn = _factory.CreateInternal();
        await conn.ExecuteAsync(sql, new
        {
            CardCode      = cardCode,
            FechaDoc      = fecha.Date,
            TransType     = transType,
            PaymentDocNum = paymentDocNum
        });
    }
}
