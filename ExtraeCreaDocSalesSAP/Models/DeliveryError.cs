namespace ExtraeCreaDocSalesSAP.Models;

/// <summary>
/// Mapea la tabla la_delivery_errors en la base de datos interna smm_dfc.
/// </summary>
public class DeliveryError
{
    public string CompanyId { get; set; } = string.Empty;
    public string Transnum { get; set; } = string.Empty;
    public int Itemnum { get; set; }
    public int Txnvoidmod { get; set; }
    public int Txnmodifier { get; set; }
    public string Storenum { get; set; } = string.Empty;
    public DateTime Itemdatetime { get; set; }
    public string Skunum { get; set; } = string.Empty;
    public string Pludesc { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal Stdunitprice { get; set; }
    public decimal Extsellprice { get; set; }
    public decimal Extundiscprice { get; set; }
    public long? DeliveryDocNum { get; set; }
    public DateTime DateCreated { get; set; } = DateTime.Now;
    public string ErrorMessage { get; set; } = string.Empty;
}
