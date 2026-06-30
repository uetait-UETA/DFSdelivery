using CreaFacturasVentasSAP.Configuration;
using CreaFacturasVentasSAP.Data;
using CreaFacturasVentasSAP.Models;
using CreaFacturasVentasSAP.Models.Sap;
using CreaFacturasVentasSAP.Repositories;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreaFacturasVentasSAP.Services;

public interface IInvoiceOrchestrator
{
    Task RunAsync(CancellationToken ct = default);

    /// <summary>
    /// Solo ejecuta la Fase A: extrae pagos del POS y los guarda en la_store_payments.
    /// Útil para backfill histórico sin crear facturas.
    /// </summary>
    Task RunPaymentsOnlyAsync(CancellationToken ct = default);
}

public class InvoiceOrchestrator : IInvoiceOrchestrator
{
    private readonly IInvoiceSalesRepository _salesRepo;
    private readonly IPaymentRepository _paymentRepo;
    private readonly IDailyInvoiceRepository _invoiceRepo;
    private readonly ISapInvoiceService _sap;
    private readonly IDbConnectionFactory _factory;
    private readonly AppSettings _settings;
    private readonly ILogger<InvoiceOrchestrator> _logger;

    public InvoiceOrchestrator(
        IInvoiceSalesRepository salesRepo,
        IPaymentRepository paymentRepo,
        IDailyInvoiceRepository invoiceRepo,
        ISapInvoiceService sap,
        IDbConnectionFactory factory,
        IOptions<AppSettings> settings,
        ILogger<InvoiceOrchestrator> logger)
    {
        _salesRepo   = salesRepo;
        _paymentRepo = paymentRepo;
        _invoiceRepo = invoiceRepo;
        _sap         = sap;
        _factory     = factory;
        _settings    = settings.Value;
        _logger      = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("Inicio del proceso de facturación diaria POS → SAP B1");

        var hasta = DateTime.Today.AddDays(-1);   // ayer
        var desde = hasta.AddDays(-(_settings.InvoicingOptions.DaysBack - 1));

        _logger.LogInformation("Rango de fechas: {Desde:yyyy-MM-dd} → {Hasta:yyyy-MM-dd}", desde, hasta);

        // ── Fase A: Extraer pagos del POS ────────────────────────────────────
        int pagosInsertados = await FaseA_ExtractPagosAsync(desde, hasta, ct);

        // ── Fases B+C: Crear facturas y cobros ───────────────────────────────
        bool loggedIn = await _sap.LoginAsync();
        if (!loggedIn)
        {
            _logger.LogError("No se pudo autenticar en SAP B1. Se omiten las fases B y C.");
            return;
        }

        int facturasCreadas  = 0;
        int cobrosCreados    = 0;
        int cobrosReintento  = 0;
        try
        {
            var tenderMappings = await _paymentRepo.GetTenderMappingsAsync();
            (facturasCreadas, cobrosCreados) =
                await FaseBC_CrearFacturasYCobrosAsync(desde, hasta, tenderMappings, ct);

            // Fase D: reintentar cobros de facturas previas sin ORCT
            cobrosReintento = await FaseD_ReintentarCobrosAsync(tenderMappings, ct);
        }
        finally
        {
            await _sap.LogoutAsync();
        }

        _logger.LogInformation(
            "Proceso finalizado — Pagos nuevos: {P} | Facturas SAP: {F} | Cobros SAP: {C} | Cobros reintentados: {R}",
            pagosInsertados, facturasCreadas, cobrosCreados, cobrosReintento);
        _logger.LogInformation("═══════════════════════════════════════════════════════");
    }

    public async Task RunPaymentsOnlyAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("BACKFILL PAGOS: extrayendo formas de pago del POS...");

        var hasta = DateTime.Today.AddDays(-1);
        var desde = hasta.AddDays(-(_settings.InvoicingOptions.DaysBack - 1));

        _logger.LogInformation("Rango de fechas: {Desde:yyyy-MM-dd} → {Hasta:yyyy-MM-dd}", desde, hasta);

        int insertados = await FaseA_ExtractPagosAsync(desde, hasta, ct);

        _logger.LogInformation(
            "Backfill pagos finalizado — Pagos nuevos insertados: {N}", insertados);
        _logger.LogInformation("═══════════════════════════════════════════════════════");
    }

    // ── FASE A: Extraer pagos del POS ─────────────────────────────────────────

    private async Task<int> FaseA_ExtractPagosAsync(
        DateTime desde, DateTime hasta, CancellationToken ct)
    {
        _logger.LogInformation("Fase A: extrayendo pagos del POS...");

        var existingTransnums = await _paymentRepo.GetExistingTransnumsAsync();

        // Las BDs están en servidores distintos → extracción en memoria
        // (lee de POS externa y cruza con la_store_sales interna por CashDrawerID+Fecha+Secuencia)
        List<StoreTender> nuevos = await ExtractPagosEnMemoriaAsync(desde, hasta, existingTransnums);

        _logger.LogInformation("Pagos nuevos a insertar: {Count}", nuevos.Count);

        if (nuevos.Count > 0)
            await _paymentRepo.InsertManyAsync(nuevos);

        return nuevos.Count;
    }

    private async Task<List<StoreTender>> ExtractPagosEnMemoriaAsync(
        DateTime desde, DateTime hasta, HashSet<string> existingTransnums)
    {
        // Une tender con la vista para obtener SecuenciaTransaccion real
        // (UserDefinedSequenceNumber ≠ SecuenciaTransaccion)
        const string sqlPos = """
            SELECT DISTINCT
                t.TransactionID,
                t.TenderID,
                t.Amount,
                t.CurrencyID,
                t.LineType,
                ISNULL(t.IsChange, 0)               AS IsChange,
                t.SiteID,
                CAST(t.CashDrawerID AS NVARCHAR(20)) AS CashDrawerID,
                CAST(t.BusinessDayDate AS DATE)       AS BusinessDayDate,
                v.SecuenciaTransaccion
            FROM [dbo].[POS_TransactionTender] t
            INNER JOIN (
                SELECT DISTINCT TransactionID, SecuenciaTransaccion
                FROM [dbo].[vw_POS_VentaItems]
            ) v ON v.TransactionID = t.TransactionID
            WHERE CAST(t.BusinessDayDate AS DATE) BETWEEN @Desde AND @Hasta
            """;

        const string sqlSales = """
            SELECT DISTINCT
                transnum              AS Transnum,
                NUMSERIE              AS Numserie,
                Numalbaran,
                CAST(itemdatetime AS DATE) AS FechaDoc,
                CompanyId
            FROM [dbo].[la_store_sales]
            WHERE CAST(itemdatetime AS DATE) BETWEEN @Desde AND @Hasta
            """;

        await using var posConn      = _factory.CreateExternal();
        await using var internalConn = _factory.CreateInternal();

        var posTenders = (await posConn.QueryAsync<PosTenderRaw>(sqlPos,
            new { Desde = desde.Date, Hasta = hasta.Date })).ToList();

        var salesRows = (await internalConn.QueryAsync<SalesTransRef>(sqlSales,
            new { Desde = desde.Date, Hasta = hasta.Date })).ToList();

        _logger.LogInformation(
            "POS_TransactionTender: {PT} registros | la_store_sales: {SS} transacciones",
            posTenders.Count, salesRows.Count);

        var salesIndex = salesRows
            .GroupBy(s => $"{s.Numserie}|{s.FechaDoc:yyyy-MM-dd}|{s.Numalbaran}")
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<StoreTender>();

        foreach (var t in posTenders)
        {
            var key = $"{t.CashDrawerID}|{t.BusinessDayDate:yyyy-MM-dd}|{t.SecuenciaTransaccion}";

            if (!salesIndex.TryGetValue(key, out var sale))
                continue;

            if (existingTransnums.Contains(sale.Transnum))
                continue;

            result.Add(new StoreTender
            {
                CompanyId       = sale.CompanyId,
                Transnum        = sale.Transnum,
                TransactionID   = t.TransactionID,
                TenderID        = t.TenderID ?? string.Empty,
                Amount          = t.Amount,
                CurrencyID      = t.CurrencyID ?? string.Empty,
                LineType        = t.LineType ?? string.Empty,
                IsChange        = t.IsChange,
                SiteID          = t.SiteID ?? string.Empty,
                CashDrawerID    = t.CashDrawerID ?? string.Empty,
                BusinessDayDate = t.BusinessDayDate
            });
        }

        return result;
    }

    // ── FASES B + C: Crear facturas y cobros ──────────────────────────────────

    private async Task<(int facturas, int cobros)> FaseBC_CrearFacturasYCobrosAsync(
        DateTime desde, DateTime hasta,
        Dictionary<string, Models.TenderSapMapping> tenderMappings,
        CancellationToken ct)
    {
        _logger.LogInformation("Fase B: creando facturas SAP...");

        var items = (await _salesRepo.GetItemsParaFacturarAsync(desde, hasta)).ToList();
        _logger.LogInformation("Ítems con ODLN pendientes de facturar: {Count}", items.Count);

        if (items.Count == 0) return (0, 0);

        // Agrupar por (CardCode, FechaDoc, TransType, TipoMovimiento)
        // SAL = ventas (ODLN→OINV) | RET = devoluciones (ORDN→ORIN)
        var grupos = items
            .GroupBy(i => $"{i.CardCode}|{i.FechaDoc:yyyy-MM-dd}|{i.TransType}|{(i.EsDevolucion ? "RET" : "SAL")}")
            .ToList();

        _logger.LogInformation("Grupos a facturar: {Count}", grupos.Count);

        int facturasCreadas = 0;
        int cobrosCreados   = 0;

        foreach (var grupo in grupos)
        {
            if (ct.IsCancellationRequested) break;

            var sample    = grupo.First();
            var cardCode  = sample.CardCode;
            var fecha     = sample.Itemdatetime.Date;
            var transType = sample.TransType;
            var bplId     = sample.BPLId;
            var uType     = transType.Equals("DP", StringComparison.OrdinalIgnoreCase)
                ? "Duty Paid" : "Duty Free";

            _logger.LogDebug(
                "Procesando grupo: CardCode={CC} | Fecha={F:yyyy-MM-dd} | Tipo={T} | {N} ítems",
                cardCode, fecha, transType, grupo.Count());

            // Validación 1: ¿Hay entregas pendientes?
            if (await _salesRepo.HayEntregasPendientesAsync(cardCode, fecha, transType))
            {
                _logger.LogWarning(
                    "BLOQUEADO — hay entregas pendientes para {CC}/{F:yyyy-MM-dd}/{T}. " +
                    "Se procesará cuando todas las entregas estén completas.",
                    cardCode, fecha, transType);
                continue;
            }

            // Validación 2: ¿Hay errores de entrega pendientes?
            if (await _salesRepo.HayErroresPendientesAsync(cardCode, fecha))
            {
                _logger.LogWarning(
                    "BLOQUEADO — hay errores en la_delivery_errors para {CC}/{F:yyyy-MM-dd}. " +
                    "Corrígelos antes de facturar.",
                    cardCode, fecha);
                continue;
            }

            var esDevolucion = sample.EsDevolucion;

            // Construir líneas consultando SAP: ODLN→BaseType15 para ventas, ORDN→BaseType16 para devoluciones
            var (invoiceLines, freights) = await BuildInvoiceLinesAsync(grupo.ToList(), esDevolucion);

            if (invoiceLines.Count == 0)
            {
                _logger.LogWarning(
                    "BLOQUEADO — todas las líneas del ODLN ya están facturadas (bost_Close) o no hay " +
                    "líneas disponibles para {CC}/{F:yyyy-MM-dd}/{T}. Requiere resolución manual en SAP B1.",
                    cardCode, fecha, transType);
                await _invoiceRepo.MarkAsErrorAsync(cardCode, fecha, transType,
                    "Todas las líneas del ODLN ya fueron facturadas (bost_Close). Resolución manual requerida.");
                continue;
            }

            var invoiceRequest = new SapInvoiceRequest
            {
                CardCode                    = cardCode,
                BPL_IDAssignedToInvoice     = bplId,
                DocDate                     = fecha.ToString("yyyy-MM-dd"),
                TaxDate                     = fecha.ToString("yyyy-MM-dd"),
                DocDueDate                  = fecha.ToString("yyyy-MM-dd"),
                NumAtCard                   = fecha.ToString("yyyy-MM-dd"),
                U_Type                      = uType,
                Comments                    = $"Automatically generated by POS integration process on {DateTime.Now:yyyy-MM-dd HH:mm:ss}.",
                DocumentLines               = invoiceLines,
                DocumentAdditionalExpenses  = freights.Count > 0 ? freights : null
            };

            try
            {
                var (docNum, docEntry) = esDevolucion
                    ? await _sap.CreateCreditNoteAsync(invoiceRequest)
                    : await _sap.CreateInvoiceAsync(invoiceRequest);

                var tipoDoc = esDevolucion ? "ORIN (Nota de Crédito)" : "OINV (Factura)";
                _logger.LogInformation(
                    "{Tipo} creada: DocNum={DN} DocEntry={DE} | {CC}/{F:yyyy-MM-dd}/{T}",
                    tipoDoc, docNum, docEntry, cardCode, fecha, transType);

                // Las devoluciones (ORIN) no tienen cobro; no actualizar la_daily_invoices
                // para evitar sobreescribir el DocEntry del OINV con el de la ORIN.
                if (!esDevolucion)
                {
                    await _invoiceRepo.UpsertAsync(new DailyInvoice
                    {
                        CompanyId       = sample.CompanyId,
                        CardCode        = cardCode,
                        BPLId           = bplId,
                        FechaDoc        = fecha,
                        TransType       = transType,
                        InvoiceDocNum   = docNum,
                        InvoiceDocEntry = docEntry,
                        ErrorMessage    = null
                    });
                }

                facturasCreadas++;

                // Fase C: crear cobro solo para ventas (no devoluciones)
                if (!esDevolucion)
                {
                    bool cobroOk = await FaseC_CrearCobroAsync(
                        cardCode, fecha, transType, bplId, docEntry, tenderMappings);
                    if (cobroOk) cobrosCreados++;
                }
            }
            catch (SapInvoiceException ex)
            {
                _logger.LogError(
                    "Error SAP creando factura {CC}/{F:yyyy-MM-dd}/{T}: {Msg}",
                    cardCode, fecha, transType, ex.Message);
                await _invoiceRepo.MarkAsErrorAsync(cardCode, fecha, transType, ex.Message);
            }
        }

        return (facturasCreadas, cobrosCreados);
    }

    private async Task<(List<SapInvoiceLine> Lines, List<SapDocumentExpense> Expenses)> BuildInvoiceLinesAsync(
        List<InvoiceSalesItem> items, bool esDevolucion)
    {
        var lines       = new List<SapInvoiceLine>();
        var allExpenses = new List<SapDocumentExpense>();
        int baseType    = esDevolucion ? 16 : 15;
        var tipoDoc     = esDevolucion ? "ORDN" : "ODLN";

        var porDocEntry = items.GroupBy(i => i.DeliveryDocEntry).ToList();

        foreach (var grupo in porDocEntry)
        {
            var docEntry = (int)grupo.Key;

            var docData = esDevolucion
                ? await _sap.GetReturnLinesAsync(docEntry)
                : await _sap.GetDeliveryLinesAsync(docEntry);

            if (docData.Lines.Count == 0)
            {
                _logger.LogWarning("No se obtuvieron líneas del {Tipo} DocEntry={DE}", tipoDoc, docEntry);
                return ([], []);
            }

            foreach (var sapLine in docData.Lines)
                lines.Add(new SapInvoiceLine { BaseType = baseType, BaseEntry = docEntry, BaseLine = sapLine.LineNum });

            allExpenses.AddRange(docData.Expenses);
        }

        // Consolidar freights: un OINV puede cubrir varios ODLN; sumar por ExpnsCode
        var expenses = allExpenses
            .GroupBy(e => e.ExpnsCode)
            .Select(g =>
            {
                var first = g.First();
                return new SapDocumentExpense
                {
                    ExpnsCode  = g.Key,
                    LineTotal  = g.Sum(e => e.LineTotal),
                    TaxCode    = first.TaxCode,
                    DistMethod = first.DistMethod,
                    Billable   = first.Billable,
                    VatGroup   = first.VatGroup
                };
            })
            .ToList();

        return (lines, expenses);
    }

    // ── FASE C: Crear cobro (Incoming Payment) ────────────────────────────────

    private async Task<bool> FaseC_CrearCobroAsync(
        string cardCode, DateTime fecha, string transType,
        int bplId, long invoiceDocEntry,
        Dictionary<string, Models.TenderSapMapping> tenderMappings)
    {
        _logger.LogDebug("Fase C: creando cobro para {CC}/{F:yyyy-MM-dd}/{T}",
            cardCode, fecha, transType);

        var pagos = (await _paymentRepo.GetPagosParaFacturaAsync(cardCode, fecha, transType))
            .ToList();

        if (pagos.Count == 0)
        {
            _logger.LogWarning(
                "No hay pagos en la_store_payments para {CC}/{F:yyyy-MM-dd}/{T}. " +
                "Cobro no creado.", cardCode, fecha, transType);
            return false;
        }

        if (tenderMappings.Count == 0)
        {
            _logger.LogWarning(
                "ADR_TENDER_SAP está vacía. Rellena la tabla y vuelve a ejecutar " +
                "para crear el cobro de {CC}/{F:yyyy-MM-dd}/{T}.", cardCode, fecha, transType);
            return false;
        }

        decimal totalPagado = pagos.Sum(p => p.Amount);

        if (totalPagado <= 0)
        {
            _logger.LogWarning(
                "Total de pagos ≤ 0 ({Tot:N2}) para {CC}/{F:yyyy-MM-dd}/{T}. " +
                "Cobro omitido (devoluciones cancelan ventas).",
                totalPagado, cardCode, fecha, transType);
            return false;
        }

        var request = new SapIncomingPaymentRequest
        {
            CardCode  = cardCode,
            DocDate   = fecha.ToString("yyyy-MM-dd"),
            BPLID     = bplId > 0 ? bplId : null,
            PaymentInvoices =
            [
                new SapPaymentInvoice
                {
                    LineNum      = 0,
                    DocEntry     = (int)invoiceDocEntry,
                    InvoiceType  = "it_Invoice",
                    SumApplied   = totalPagado
                }
            ]
        };

        // Agrupar pagos por TenderID y construir el detalle de pago
        var porTender = pagos.GroupBy(p => p.TenderID, StringComparer.OrdinalIgnoreCase);
        bool algunSinMapeo = false;

        foreach (var tenderGrupo in porTender)
        {
            if (!tenderMappings.TryGetValue(tenderGrupo.Key, out var map))
            {
                _logger.LogWarning(
                    "TenderID '{T}' no tiene mapeo en ADR_TENDER_SAP. " +
                    "Agrega la cuenta SAP correspondiente.", tenderGrupo.Key);
                algunSinMapeo = true;
                continue;
            }

            decimal suma = tenderGrupo.Sum(p => p.Amount);

            switch (map.PaymentType.ToUpperInvariant())
            {
                case "CASH":
                    if (string.IsNullOrWhiteSpace(map.SapAccount))
                    {
                        _logger.LogWarning(
                            "TenderID '{T}' (CASH): SapAccount vacío en ADR_TENDER_SAP.", tenderGrupo.Key);
                        algunSinMapeo = true;
                        continue;
                    }
                    request.CashAccount = map.SapAccount;
                    request.CashSum     = (request.CashSum ?? 0) + suma;
                    _logger.LogInformation("  Tender {T}: Cash {M:N2} → cuenta '{C}'", tenderGrupo.Key, suma, map.SapAccount);
                    break;

                case "CREDITCARD":
                    if (!int.TryParse(map.SapAccount, out var ccCode))
                    {
                        _logger.LogWarning(
                            "TenderID '{T}' (CREDITCARD): SapAccount='{A}' no es código numérico válido.",
                            tenderGrupo.Key, map.SapAccount);
                        algunSinMapeo = true;
                        continue;
                    }
                    request.PaymentCreditCards ??= [];
                    // Agregar al mismo código si ya existe (varios tenders → mismo código SAP)
                    var existingCc = request.PaymentCreditCards.FirstOrDefault(cc => cc.CreditCard == ccCode);
                    if (existingCc != null)
                        existingCc.CreditSum += suma;
                    else
                        request.PaymentCreditCards.Add(new SapPaymentCreditCard { CreditCard = ccCode, CreditSum = suma });
                    _logger.LogInformation("  Tender {T}: CreditCard {M:N2} → código {C}", tenderGrupo.Key, suma, ccCode);
                    break;

                case "CHECK":
                case "TRANSFER":
                    if (string.IsNullOrWhiteSpace(map.SapAccount))
                    {
                        _logger.LogWarning(
                            "TenderID '{T}' ({Tipo}): SapAccount vacío en ADR_TENDER_SAP.", tenderGrupo.Key, map.PaymentType);
                        algunSinMapeo = true;
                        continue;
                    }
                    request.PaymentChecks ??= [];
                    request.PaymentChecks.Add(new SapPaymentCheck
                    {
                        AccountNo = map.SapAccount,
                        CheckSum  = suma
                    });
                    _logger.LogInformation("  Tender {T}: {Tipo} {M:N2} → AccountNo='{C}'",
                        tenderGrupo.Key, map.PaymentType, suma, map.SapAccount);
                    break;

                default:
                    _logger.LogWarning(
                        "TenderID '{T}': PaymentType='{P}' desconocido en ADR_TENDER_SAP. " +
                        "Usa CASH, CREDITCARD, CHECK o TRANSFER.", tenderGrupo.Key, map.PaymentType);
                    algunSinMapeo = true;
                    continue;
            }
        }

        if (algunSinMapeo)
        {
            _logger.LogWarning(
                "Cobro de {CC}/{F:yyyy-MM-dd}/{T} omitido por TenderIDs sin mapeo o SapAccount inválido.",
                cardCode, fecha, transType);
            return false;
        }

        _logger.LogInformation(
            "Enviando cobro {CC}/{F:yyyy-MM-dd}/{T}: Total={Tot:N2} | " +
            "Cash={Cash} | CreditCards={NCc} | Checks/Transfers={NChk}",
            cardCode, fecha, transType, totalPagado,
            request.CashSum.HasValue ? $"{request.CashSum:N2} ({request.CashAccount})" : "-",
            request.PaymentCreditCards?.Count ?? 0,
            request.PaymentChecks?.Count ?? 0);

        try
        {
            int paymentDocNum = await _sap.CreateIncomingPaymentAsync(request);

            _logger.LogInformation(
                "Cobro creado: DocNum={DN} | Total={Total:N2} | {CC}/{F:yyyy-MM-dd}/{T}",
                paymentDocNum, totalPagado, cardCode, fecha, transType);

            await _invoiceRepo.SetPaymentAsync(cardCode, fecha, transType, paymentDocNum);
            return true;
        }
        catch (SapInvoiceException ex)
        {
            _logger.LogError(
                "Error SAP creando cobro para {CC}/{F:yyyy-MM-dd}/{T}: {Msg}",
                cardCode, fecha, transType, ex.Message);
            return false;
        }
    }

    // ── FASE D: Reintentar cobros de facturas previas sin ORCT ───────────────────

    private async Task<int> FaseD_ReintentarCobrosAsync(
        Dictionary<string, Models.TenderSapMapping> tenderMappings, CancellationToken ct)
    {
        var pendientes = (await _invoiceRepo.GetFacturasSinCobroAsync()).ToList();

        _logger.LogInformation("Fase D: {Count} facturas sin cobro pendientes.", pendientes.Count);

        if (pendientes.Count == 0) return 0;

        int cobros = 0;
        foreach (var inv in pendientes)
        {
            if (ct.IsCancellationRequested) break;

            bool ok = await FaseC_CrearCobroAsync(
                inv.CardCode, inv.FechaDoc, inv.TransType,
                inv.BPLId, inv.InvoiceDocEntry!.Value, tenderMappings);

            if (ok) cobros++;
        }

        return cobros;
    }

    // ── DTOs internos para la extracción de pagos ─────────────────────────────

    private class PosTenderRaw
    {
        public long TransactionID { get; set; }
        public string? TenderID { get; set; }
        public decimal Amount { get; set; }
        public string? CurrencyID { get; set; }
        public string? LineType { get; set; }
        public bool IsChange { get; set; }
        public string? SiteID { get; set; }
        public string? CashDrawerID { get; set; }
        public DateTime BusinessDayDate { get; set; }
        public long? SecuenciaTransaccion { get; set; }
    }

    private class SalesTransRef
    {
        public string Transnum { get; set; } = string.Empty;
        public string Numserie { get; set; } = string.Empty;
        public long Numalbaran { get; set; }
        public DateTime FechaDoc { get; set; }
        public int CompanyId { get; set; }
    }
}
