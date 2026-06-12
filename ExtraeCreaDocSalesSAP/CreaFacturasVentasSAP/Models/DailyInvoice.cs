namespace CreaFacturasVentasSAP.Models;

/// <summary>
/// Representa una factura diaria consolidada en la_daily_invoices.
/// InvoiceDocNum: NULL=pendiente, -1=error, >0=DocNum SAP.
/// </summary>
public class DailyInvoice
{
    public long ID { get; set; }
    public int CompanyId { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public int BPLId { get; set; }
    public DateTime FechaDoc { get; set; }
    public string TransType { get; set; } = string.Empty;   // DF | DP
    public long? InvoiceDocNum { get; set; }
    public long? InvoiceDocEntry { get; set; }
    public long? PaymentDocNum { get; set; }
    public DateTime DateCreated { get; set; }
    public DateTime? DateUpdated { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Grupo de ODLNs a consolidar en una única factura.
/// </summary>
public class InvoiceGroup
{
    public int CompanyId { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public int BPLId { get; set; }
    public DateTime FechaDoc { get; set; }
    public string TransType { get; set; } = string.Empty;   // DF | DP
    public string WhsCode { get; set; } = string.Empty;
    public List<OdlnLine> Lines { get; set; } = [];
}

/// <summary>
/// Línea de un ODLN existente — para copiarla a la factura OINV.
/// BaseLine = LineNum del ODLN en SAP (0, 1, 2...).
/// </summary>
public class OdlnLine
{
    public long DocEntry { get; set; }
    public int BaseLine { get; set; }
}
