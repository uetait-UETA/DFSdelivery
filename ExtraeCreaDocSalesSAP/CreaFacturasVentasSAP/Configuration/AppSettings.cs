namespace CreaFacturasVentasSAP.Configuration;

public class AppSettings
{
    public SapServiceLayerSettings SapServiceLayer { get; set; } = new();
    public InvoicingOptions InvoicingOptions { get; set; } = new();
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

public class InvoicingOptions
{
    /// <summary>
    /// Días hacia atrás a procesar (1 = solo ayer, 7 = última semana).
    /// El proceso busca facturas pendientes desde hoy-DaysBack hasta ayer.
    /// </summary>
    public int DaysBack { get; set; } = 30;
}
