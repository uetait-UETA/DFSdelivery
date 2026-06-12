using System.Text.Json.Serialization;

namespace ExtraeCreaDocSalesSAP.Models.Sap;

// ── Login ──────────────────────────────────────────────────────────────────────

public class SapLoginRequest
{
    public string CompanyDB { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

// ── Inventory ─────────────────────────────────────────────────────────────────

// SAP B1 SL retorna Items('X')/ItemWarehouseInfoCollection como el Item completo con
// ItemWarehouseInfoCollection como propiedad anidada (no el formato OData estándar value:[])
public class SapItemStockResponse
{
    [JsonPropertyName("ItemWarehouseInfoCollection")]
    public List<ItemWarehouseInfo>? ItemWarehouseInfoCollection { get; set; }
}

public class ItemWarehouseInfo
{
    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("InStock")]
    public decimal InStock { get; set; }

    [JsonPropertyName("Committed")]
    public decimal Committed { get; set; }

    public decimal Available => InStock - Committed;
}

// ── Document creation (ODLN / ORDN) ──────────────────────────────────────────

public class SapDocumentRequest
{
    public string CardCode { get; set; } = string.Empty;

    /// <summary>BPL_ID de la sucursal SAP B1 (Business Place).</summary>
    public int BPL_IDAssignedToInvoice { get; set; }

    public string DocDate { get; set; } = string.Empty;
    public string TaxDate { get; set; } = string.Empty;
    public string DocDueDate { get; set; } = string.Empty;

    /// <summary>DF → "Duty Free" | DP → "Duty Paid"</summary>
    public string? U_Type { get; set; }

    /// <summary>Texto libre visible en el documento SAP B1.</summary>
    public string? Comments { get; set; }

    /// <summary>Número de serie de la caja/terminal POS.</summary>
    public string? U_BOL { get; set; }

    /// <summary>Número de albarán del POS (referencia externa del documento).</summary>
    public string? NumAtCard { get; set; }

    public List<SapDocumentLine> DocumentLines { get; set; } = [];

    /// <summary>
    /// Gastos adicionales (freight) para registrar impuestos en cuentas específicas.
    /// Solo se incluye si hay importes de impuesto mayores a 0.
    /// </summary>
    public List<SapAdditionalExpense>? DocumentAdditionalExpenses { get; set; }
}

public class SapDocumentLine
{
    public string ItemCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }

    /// <summary>Precio unitario NETO (sin impuesto). El impuesto va en DocumentAdditionalExpenses.</summary>
    public decimal UnitPrice { get; set; }

    public string WarehouseCode { get; set; } = string.Empty;
}

/// <summary>
/// Entrada en DocumentAdditionalExpenses para registrar impuestos
/// en la cuenta de freight/gasto configurada en SAP B1.
/// </summary>
public class SapAdditionalExpense
{
    public int ExpenseCode { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>dNone = el monto va directo a la cuenta de freight sin distribuirse a líneas.</summary>
    public string DistributionMethod { get; set; } = "dNone";
}

// ── Document response ─────────────────────────────────────────────────────────

public class SapDocumentResponse
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("CardCode")]
    public string? CardCode { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }
}

// ── OData list response (para $filter en colecciones) ────────────────────────

public class SapODataList<T>
{
    [JsonPropertyName("value")]
    public List<T>? Value { get; set; }
}

// ── Error response ────────────────────────────────────────────────────────────

public class SapErrorResponse
{
    [JsonPropertyName("error")]
    public SapErrorDetail? Error { get; set; }
}

public class SapErrorDetail
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public SapErrorMessage? Message { get; set; }
}

public class SapErrorMessage
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
