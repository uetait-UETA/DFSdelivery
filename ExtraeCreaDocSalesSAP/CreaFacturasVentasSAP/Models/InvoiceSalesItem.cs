namespace CreaFacturasVentasSAP.Models;

/// <summary>
/// Proyección de la_store_sales con solo los campos necesarios para facturación.
/// </summary>
public class InvoiceSalesItem
{
    public long ID { get; set; }
    public int CompanyId { get; set; }
    public string Transnum { get; set; } = string.Empty;
    public int Itemnum { get; set; }
    public string Storenum { get; set; } = string.Empty;
    public string Numserie { get; set; } = string.Empty;
    public DateTime Itemdatetime { get; set; }
    public string CardCode { get; set; } = string.Empty;
    public int BPLId { get; set; }
    public string WhsCode { get; set; } = string.Empty;
    public string TransType { get; set; } = string.Empty;
    public long DeliveryDocNum { get; set; }
    public long DeliveryDocEntry { get; set; }
    public int Txnmodifier { get; set; }

    public bool EsDevolucion => Txnmodifier == 1;
    public DateOnly FechaDoc => DateOnly.FromDateTime(Itemdatetime);
}
