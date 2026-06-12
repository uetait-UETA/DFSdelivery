namespace CreaFacturasVentasSAP.Models;

/// <summary>
/// Representa un pago del POS almacenado en la_store_payments.
/// </summary>
public class StoreTender
{
    public long ID { get; set; }
    public int CompanyId { get; set; }
    public string Transnum { get; set; } = string.Empty;
    public long TransactionID { get; set; }
    public string TenderID { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string CurrencyID { get; set; } = string.Empty;
    public string LineType { get; set; } = string.Empty;
    public bool IsChange { get; set; }
    public string SiteID { get; set; } = string.Empty;
    public string CashDrawerID { get; set; } = string.Empty;
    public DateTime BusinessDayDate { get; set; }
}

/// <summary>
/// Mapeo TenderID → cuenta SAP de la tabla ADR_TENDER_SAP.
/// </summary>
public class TenderSapMapping
{
    public string TenderID { get; set; } = string.Empty;
    public string SapAccount { get; set; } = string.Empty;
    public string PaymentType { get; set; } = string.Empty;  // Cash | CreditCard | Check | Transfer
    public string? Description { get; set; }
}
