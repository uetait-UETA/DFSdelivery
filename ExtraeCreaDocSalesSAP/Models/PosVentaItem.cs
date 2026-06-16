namespace ExtraeCreaDocSalesSAP.Models;

/// <summary>
/// Mapea las columnas relevantes de la vista vw_POS_VentaItems del servidor externo (DB: POS).
/// </summary>
public class PosVentaItem
{
    // Identificación de transacción
    public long TransactionID { get; set; }
    public string SiteID { get; set; } = string.Empty;
    public string PosLoc { get; set; } = string.Empty;
    public string CashDrawerID { get; set; } = string.Empty;
    public long SecuenciaTransaccion { get; set; }
    public string TransactionViewType { get; set; } = string.Empty;
    public string TransType { get; set; } = string.Empty;   // 'DF' | 'DP'

    // Fecha
    public DateTime BusinessDayDate { get; set; }

    // Item
    public int ItemLineID { get; set; }
    public int SaleLineNumber { get; set; }
    public int LineNumber { get; set; }
    public string ItemAccion { get; set; } = string.Empty;  // 'SELL' | 'RETURN'
    public string POSItemID { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string DutyType { get; set; } = string.Empty;
    public string InventoryLocation { get; set; } = string.Empty;

    // Precios y cantidades
    public decimal Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal MontoExtendido { get; set; }
    public decimal MontoSinImpuesto { get; set; }

    // Impuesto nivel transacción
    public decimal TransactionGrandAmount { get; set; }
    public decimal TransactionNetAmount { get; set; }

    /// <summary>Impuesto exacto de la transacción (nivel documento, igual en todas las líneas).</summary>
    public decimal TransactionTaxAmount => TransactionGrandAmount - TransactionNetAmount;

    // Impuesto
    public string CodigoImpuesto { get; set; } = string.Empty;  // S1 | S2 | S4

    // Datos de devolución
    public string? DevWorkstation { get; set; }
    public string? DevNroTicket { get; set; }

    // Número de ticket impreso en caja
    public int NroTicket { get; set; }

    // Campos calculados
    public string Transnum =>
        $"{PosLoc}{CashDrawerID}{SecuenciaTransaccion}";

    public bool EsVenta      => ItemAccion.Equals("SELL",   StringComparison.OrdinalIgnoreCase);
    public bool EsDevolucion => ItemAccion.Equals("RETURN", StringComparison.OrdinalIgnoreCase);

    /// <summary>Monto del impuesto derivado de los montos (no requiere columna extra).</summary>
    public decimal MontoImpuesto => MontoExtendido - MontoSinImpuesto;
}
