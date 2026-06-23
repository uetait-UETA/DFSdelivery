using System.Text.Json.Serialization;

namespace CreaFacturasVentasSAP.Models.Sap;

// ── Login ──────────────────────────────────────────────────────────────────────

public class SapLoginRequest
{
    public string CompanyDB { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

// ── Factura / Nota de crédito (OINV / ORIN) ──────────────────────────────────

public class SapInvoiceRequest
{
    public string CardCode { get; set; } = string.Empty;
    public int BPL_IDAssignedToInvoice { get; set; }
    public string DocDate { get; set; } = string.Empty;
    public string TaxDate { get; set; } = string.Empty;
    public string DocDueDate { get; set; } = string.Empty;
    public string? U_Type { get; set; }

    /// <summary>Texto libre visible en el documento SAP B1.</summary>
    public string? Comments { get; set; }

    /// <summary>Referencia del cliente/proveedor — se fuerza a la fecha del documento para no heredar el del ODLN.</summary>
    public string? NumAtCard { get; set; }

    public List<SapInvoiceLine> DocumentLines { get; set; } = [];

    public List<SapDocumentExpense>? DocumentAdditionalExpenses { get; set; }
}

public class SapInvoiceLine
{
    /// <summary>15 = DeliveryNote (ODLN) | 16 = Returns (ORDN)</summary>
    public int BaseType { get; set; }

    /// <summary>DocEntry del documento base (ODLN o ORDN).</summary>
    public int BaseEntry { get; set; }

    /// <summary>LineNum (0-indexed) de la línea en el documento base.</summary>
    public int BaseLine { get; set; }
}

// ── Incoming Payment (ORCT) ───────────────────────────────────────────────────

public class SapIncomingPaymentRequest
{
    public string CardCode { get; set; } = string.Empty;
    public string DocDate { get; set; } = string.Empty;
    public int? BPLID { get; set; }

    public List<SapPaymentInvoice> PaymentInvoices { get; set; } = [];

    // Efectivo
    public string? CashAccount { get; set; }
    public decimal? CashSum { get; set; }

    // Tarjeta de crédito
    public List<SapPaymentCreditCard>? PaymentCreditCards { get; set; }

    // Transferencia / cheque
    public List<SapPaymentCheck>? PaymentChecks { get; set; }
}

public class SapPaymentInvoice
{
    public int LineNum { get; set; }

    /// <summary>DocEntry de la factura OINV a la que se aplica el cobro.</summary>
    public int DocEntry { get; set; }

    /// <summary>it_Invoice para facturas normales.</summary>
    public string InvoiceType { get; set; } = "it_Invoice";

    public decimal SumApplied { get; set; }
}

public class SapPaymentCreditCard
{
    /// <summary>Código de tarjeta en SAP B1 (tabla CreditCard).</summary>
    public int CreditCard { get; set; }
    public decimal CreditSum { get; set; }
    /// <summary>SAP B1 requiere este campo. Usamos fecha futura genérica para pagos POS sin fecha de vencimiento real.</summary>
    public string CardValidUntil { get; set; } = "2049-12-31";
    /// <summary>SAP B1 requiere este campo. Para pagos POS sin número de autorización usamos "POS".</summary>
    public string VoucherNum { get; set; } = "POS";
}

public class SapPaymentCheck
{
    public string AccountNo { get; set; } = string.Empty;
    public decimal CheckSum { get; set; }
}

// ── Freights / gastos adicionales ─────────────────────────────────────────────

public class SapDocumentExpense
{
    [JsonPropertyName("ExpenseCode")]
    public int ExpnsCode { get; set; }

    [JsonPropertyName("LineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("DistMethod")]
    public string? DistMethod { get; set; }

    [JsonPropertyName("Billable")]
    public string? Billable { get; set; }

    [JsonPropertyName("VatGroup")]
    public string? VatGroup { get; set; }
}

// ── Respuesta GET DeliveryNotes / Returns ─────────────────────────────────────

public class SapDeliveryNoteResponse
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<SapDeliveryLine>? DocumentLines { get; set; }

    [JsonPropertyName("DocumentAdditionalExpenses")]
    public List<SapDocumentExpense>? DocumentAdditionalExpenses { get; set; }
}

public class SapDeliveryLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }
}

/// <summary>Datos combinados de un ODLN/ORDN: líneas de ítems + freights.</summary>
public class SapDocumentData
{
    public List<SapDeliveryLine> Lines    { get; init; } = [];
    public List<SapDocumentExpense> Expenses { get; init; } = [];
}

// ── Respuesta de creación ─────────────────────────────────────────────────────

public class SapDocumentResponse
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }
}

// ── Respuesta de error ────────────────────────────────────────────────────────

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
