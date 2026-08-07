namespace ExtraeCreaDocSalesSAP.Configuration;

public class AppSettings
{
    public SapServiceLayerSettings SapServiceLayer { get; set; } = new();
    public ProcessingOptions ProcessingOptions { get; set; } = new();
    public FreightSettings FreightSettings { get; set; } = new();
}

public class SapServiceLayerSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string CompanyDB { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;
    public bool IgnoreSslErrors { get; set; } = false;
}

public class ProcessingOptions
{
    public int BatchSize { get; set; } = 500;
    public string FilterViewType { get; set; } = "RetailTransactionStockView";

    /// <summary>
    /// SKUs que se incluyen en el ODLN pero se saltan la validación de stock.
    /// Útil para ítems de tipo servicio/cargo sin inventario físico en SAP B1.
    /// </summary>
    public List<string> SkipStockCheckItemCodes { get; set; } = new();
}

/// <summary>
/// Configuración de los gastos adicionales (freight) enviados a SAP B1 al crear ODLN/ORDN.
/// ExpnsCode=1: Sales Tax (monto exacto de la transacción desde la vista POS).
/// ExpnsCode=2: Tax DEEE (MontoExtendido del ítem específico DeeeItemCode).
/// </summary>
public class FreightSettings
{
    public int SalesTaxExpnsCode { get; set; } = 1;
    public int DeeeTaxExpnsCode { get; set; } = 2;

    /// <summary>POSItemID del ítem que genera el impuesto DEEE. Vacío = DEEE deshabilitado.</summary>
    public string DeeeItemCode { get; set; } = string.Empty;
}
