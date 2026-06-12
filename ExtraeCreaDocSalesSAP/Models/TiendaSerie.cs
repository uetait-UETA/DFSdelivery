namespace ExtraeCreaDocSalesSAP.Models;

/// <summary>
/// Mapea la tabla ADR_TIENDA_SERIE en smm_dfc.
/// CompanyId almacena el BPL_ID (Business Place) de SAP B1 (3 o 4).
/// </summary>
public class TiendaSerie
{
    public string WhsCode { get; set; } = string.Empty;   // WHSCODE
    public string CardCode { get; set; } = string.Empty;  // CARCODE
    public string NumSerie { get; set; } = string.Empty;  // NUMSERIE (= CashDrawerID)
    public int NumStore { get; set; }                      // NUMSTORE (= PosLoc)
    public string CompanyId { get; set; } = string.Empty; // CompanyId → contiene el BPL_ID
    public string DutyType { get; set; } = string.Empty;  // DUTYTYPE ('DF' | 'DP')

    /// <summary>BPL_ID de la sucursal en SAP B1 (derivado de CompanyId).</summary>
    public int BPLId => int.TryParse(CompanyId, out var id) ? id : 0;

    public string Key => BuildKey(NumSerie, NumStore, DutyType);

    public static string BuildKey(string numSerie, int numStore, string dutyType) =>
        $"{numSerie}|{numStore}|{dutyType}".ToUpperInvariant();
}
