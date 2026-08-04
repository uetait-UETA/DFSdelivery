namespace ExtraeCreaDocSalesSAP.Models;

/// <summary>
/// Ítem con error de inventario, listo para re-verificar stock en SAP B1.
/// Resultado del JOIN entre la_delivery_errors y la_store_sales.
/// </summary>
public class StockErrorItem
{
    public string Transnum  { get; set; } = string.Empty;
    public string Skunum    { get; set; } = string.Empty;
    public decimal Qty      { get; set; }
    public string DutyType  { get; set; } = string.Empty;
    public string Numserie  { get; set; } = string.Empty;
    public string Storenum  { get; set; } = string.Empty;

    /// <summary>Bodega override manual. Si tiene valor, tiene prioridad sobre ADR_TIENDA_SERIE.</summary>
    public string? WhsCode  { get; set; }
}

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
