namespace ExtraeCreaDocSalesSAP.Models;

/// <summary>
/// Mapea la tabla la_store_sales en smm_dfc.
/// DeliveryDocNum: NULL = pendiente, > 0 = DocNum SAP, -1 = error.
/// </summary>
public class StoreSale
{
    public long ID { get; set; }
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
    public long? DeliveryDocEntry { get; set; }
    public string Numserie { get; set; } = string.Empty;
    public long Numalbaran { get; set; }
    public string Abonode_Numalbaran { get; set; } = string.Empty;
    public string Abonode_Numserie { get; set; } = string.Empty;
    public string TransType { get; set; } = "DF";
    public string CodigoImpuesto { get; set; } = string.Empty;

    /// <summary>'DF' (Duty Free) o 'DP' (Duty Paid) — tipo del ítem individual.</summary>
    public string DutyType { get; set; } = string.Empty;

    /// <summary>Impuesto exacto de la transacción (TransactionGrandAmount - TransactionNetAmount). Igual en todas las líneas de la misma txn.</summary>
    public decimal SalesTaxAmount { get; set; }

    /// <summary>Concatenación de SiteID-CashDrawerID-NroTicket del POS.</summary>
    public string TicketNo { get; set; } = string.Empty;

    /// <summary>
    /// Bodega override seleccionada manualmente desde la pantalla de ajuste.
    /// Si tiene valor, tiene prioridad sobre ADR_TIENDA_SERIE para el stock check y la línea del ODLN.
    /// </summary>
    public string? WhsCode { get; set; }

    public bool EsDevolucion => Txnmodifier == 1;

    /// <summary>Impuesto derivado de los montos ya almacenados — sin columna extra.</summary>
    public decimal MontoImpuesto => Extsellprice - Extundiscprice;
}
