using ExtraeCreaDocSalesSAP.Configuration;
using ExtraeCreaDocSalesSAP.Models;
using ExtraeCreaDocSalesSAP.Models.Sap;
using ExtraeCreaDocSalesSAP.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExtraeCreaDocSalesSAP.Services;

public interface ISyncService
{
    Task<int> RunAsync(CancellationToken ct = default);
}

public class SyncService : ISyncService
{
    private readonly IPosRepository _posRepo;
    private readonly IStoreSalesRepository _salesRepo;
    private readonly ITiendaSerieRepository _tiendaRepo;
    private readonly ISapService _sap;
    private readonly AppSettings _settings;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IPosRepository posRepo,
        IStoreSalesRepository salesRepo,
        ITiendaSerieRepository tiendaRepo,
        ISapService sap,
        IOptions<AppSettings> settings,
        ILogger<SyncService> logger)
    {
        _posRepo    = posRepo;
        _salesRepo  = salesRepo;
        _tiendaRepo = tiendaRepo;
        _sap        = sap;
        _settings   = settings.Value;
        _logger     = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════");
        _logger.LogInformation("Inicio del proceso de sincronización POS → SAP B1");

        var mappings = await _tiendaRepo.GetAllMappingsAsync();
        if (mappings.Count == 0)
        {
            _logger.LogError("ADR_TIENDA_SERIE está vacía. Configura los mapeos antes de ejecutar.");
            return 0;
        }

        var warehouseLists = await _tiendaRepo.GetWarehouseListsAsync();

        int insertados = await ExtractAndStoreAsync(mappings, ct);

        int procesados = 0;
        bool loggedIn  = await _sap.LoginAsync();
        if (!loggedIn)
        {
            _logger.LogError("No se pudo autenticar en SAP B1. Se omite la Fase 2.");
        }
        else
        {
            try
            {
                await LiberarErroresStockAsync(mappings, warehouseLists, ct);
                await LiberarErroresSapAsync(ct);
                procesados = await ProcessPendingAsync(mappings, warehouseLists, ct);
            }
            finally { await _sap.LogoutAsync(); }
        }

        _logger.LogInformation(
            "Proceso finalizado — Insertados: {I} | Documentos SAP creados: {P}",
            insertados, procesados);
        _logger.LogInformation("═══════════════════════════════════════════════════════");

        return procesados;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 0 — Auto-liberación de errores de stock
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> LiberarErroresStockAsync(
        Dictionary<string, TiendaSerie> mappings,
        Dictionary<string, List<string>> warehouseLists,
        CancellationToken ct)
    {
        _logger.LogInformation("Fase 0: revisando errores de stock en la_delivery_errors...");

        var errorItems = (await _salesRepo.GetStockErrorItemsAsync()).ToList();
        if (errorItems.Count == 0)
        {
            _logger.LogInformation("Fase 0: sin errores de stock pendientes.");
            return 0;
        }

        var grupos = errorItems.GroupBy(e => e.Transnum).ToList();
        _logger.LogInformation("Fase 0: {Count} transacción(es) con error de stock.", grupos.Count);

        var skipCodesF0 = _settings.ProcessingOptions.SkipStockCheckItemCodes
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int liberadas = 0;
        foreach (var grupo in grupos)
        {
            if (ct.IsCancellationRequested) break;

            var transnum = grupo.Key;
            bool todosOk = true;

            foreach (var item in grupo)
            {
                // Prioridad máxima: ítems configurados sin validación de stock siempre pasan,
                // independientemente de si tienen WhsCode override o no.
                if (skipCodesF0.Contains(item.Skunum.Trim()))
                {
                    _logger.LogInformation(
                        "  Txn={Txn} | SKU={Sku} — stock check omitido (SkipStockCheckItemCodes)",
                        transnum, item.Skunum);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.WhsCode))
                {
                    // Override manual: verificar solo esa bodega
                    var overrideWhs    = item.WhsCode.Trim();
                    decimal disponible = await _sap.GetAvailableStockAsync(item.Skunum, overrideWhs);
                    if (disponible < item.Qty)
                    {
                        _logger.LogInformation(
                            "  Txn={Txn} | SKU={Sku} whs={Whs} (override) — Stock insuficiente (disp={D}, req={R})",
                            transnum, item.Skunum, overrideWhs, disponible, item.Qty);
                        todosOk = false;
                        break;
                    }
                    _logger.LogInformation(
                        "  Txn={Txn} | SKU={Sku} whs={Whs} (override) — Stock OK (disp={D}, req={R})",
                        transnum, item.Skunum, overrideWhs, disponible, item.Qty);
                }
                else
                {
                    // Sin override: probar todas las bodegas en orden
                    var numStore = int.TryParse(item.Storenum, out var s) ? s : 0;
                    var itemKey  = TiendaSerie.BuildKey(item.Numserie, numStore, item.DutyType);

                    if (!warehouseLists.TryGetValue(itemKey, out var whsList) || whsList.Count == 0)
                    {
                        _logger.LogWarning(
                            "Fase 0: Txn={Txn} SKU={Sku} — sin mapeo ADR_TIENDA_SERIE, sigue bloqueada.",
                            transnum, item.Skunum);
                        todosOk = false;
                        break;
                    }

                    string? whsConStock = null;
                    foreach (var whs in whsList)
                    {
                        decimal disponible = await _sap.GetAvailableStockAsync(item.Skunum, whs);
                        if (disponible >= item.Qty)
                        {
                            _logger.LogInformation(
                                "  Txn={Txn} | SKU={Sku} whs={Whs} — Stock OK (disp={D}, req={R})",
                                transnum, item.Skunum, whs, disponible, item.Qty);
                            whsConStock = whs;
                            break;
                        }
                        _logger.LogInformation(
                            "  Txn={Txn} | SKU={Sku} whs={Whs} — insuficiente (disp={D}, req={R}){Next}",
                            transnum, item.Skunum, whs, disponible, item.Qty,
                            whsList.Count > 1 ? ", prueba siguiente..." : "");
                    }

                    if (whsConStock is null)
                    {
                        todosOk = false;
                        break;
                    }

                    // Persistir la bodega encontrada para que Fase 2 la use directamente
                    await _salesRepo.UpdateWhsCodeAsync(item.Id, whsConStock);
                    _logger.LogDebug(
                        "  Txn={Txn} SKU={Sku} → WhsCode={Whs} guardado en la_store_sales",
                        transnum, item.Skunum, whsConStock);
                }
            }

            if (todosOk)
            {
                await _salesRepo.ClearErrorsByTransnumAsync(transnum);
                _logger.LogInformation("  Txn={Txn} → LIBERADA, se reintentará en Fase 2.", transnum);
                liberadas++;
            }
        }

        _logger.LogInformation(
            "Fase 0 completada — Liberadas: {L} | Siguen bloqueadas: {B}",
            liberadas, grupos.Count - liberadas);

        return liberadas;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 0-B — Auto-reintento de errores SAP
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> LiberarErroresSapAsync(CancellationToken ct)
    {
        _logger.LogInformation("Fase 0-B: revisando errores SAP para reintento...");

        var transnums = (await _salesRepo.GetSapErrorTransnumsAsync()).ToList();
        if (transnums.Count == 0)
        {
            _logger.LogInformation("Fase 0-B: sin errores SAP pendientes.");
            return 0;
        }

        _logger.LogInformation("Fase 0-B: {Count} transacción(es) con error SAP — se liberan para reintento.", transnums.Count);

        int liberados = 0;
        foreach (var transnum in transnums)
        {
            if (ct.IsCancellationRequested) break;
            await _salesRepo.ClearErrorsByTransnumAsync(transnum);
            _logger.LogInformation("  Txn={Txn} → liberada para reintento SAP.", transnum);
            liberados++;
        }

        _logger.LogInformation("Fase 0-B completada — {Count} transacción(es) liberadas.", liberados);
        return liberados;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 1
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> ExtractAndStoreAsync(
        Dictionary<string, TiendaSerie> mappings, CancellationToken ct)
    {
        _logger.LogInformation("Fase 1: extrayendo ventas del servidor POS...");

        var opts     = _settings.ProcessingOptions;
        var posItems = (await _posRepo.GetVentasAsync(opts.FilterViewType, opts.BatchSize)).ToList();
        _logger.LogInformation("Registros leídos de POS: {Count}", posItems.Count);

        if (posItems.Count == 0) return 0;

        var existingKeys = await _salesRepo.GetExistingKeysAsync();
        var nuevos       = new List<StoreSale>();
        int sinMapeo     = 0;
        int duplicados   = 0;

        int excluidos = 0;

        foreach (var p in posItems)
        {
            if (ct.IsCancellationRequested) break;

            if (existingKeys.Contains($"{p.Transnum}|{p.SaleLineNumber}"))
            {
                duplicados++;
                continue;
            }

            var itemDutyNorm = NormalizeDutyType(p.DutyType);

            // Regla: transacción DP no puede contener ítems DutyFree.
            // Excepción: el ítem DEEE (DeeeItemCode) siempre se permite — su monto
            // va al freight ExpnsCode=2, no como línea del documento.
            var deeeItemCode = _settings.FreightSettings.DeeeItemCode?.Trim();
            bool esItemDeee  = !string.IsNullOrEmpty(deeeItemCode) &&
                               p.POSItemID.Trim().Equals(deeeItemCode, StringComparison.OrdinalIgnoreCase);

            if (p.TransType.Equals("DP", StringComparison.OrdinalIgnoreCase) &&
                itemDutyNorm.Equals("DF", StringComparison.OrdinalIgnoreCase) &&
                !esItemDeee)
            {
                _logger.LogWarning(
                    "Ítem DutyFree excluido de txn DP — SKU={Sku}, Txn={Txn}",
                    p.POSItemID, p.Transnum);
                excluidos++;
                continue;
            }

            // El mapeo de tienda usa TransType de la txn para obtener CardCode/BPLId
            // (ambas entradas DF/DP comparten los mismos CardCode y CompanyId)
            var key = TiendaSerie.BuildKey(
                p.CashDrawerID,
                int.TryParse(p.PosLoc, out var pl) ? pl : 0,
                p.TransType);

            if (!mappings.TryGetValue(key, out var tienda))
            {
                _logger.LogWarning(
                    "Sin mapeo ADR_TIENDA_SERIE — Serie={S}, Store={St}, DutyType={D}. " +
                    "Txn={Txn} omitida.", p.CashDrawerID, p.PosLoc, p.TransType, p.Transnum);
                sinMapeo++;
                continue;
            }

            nuevos.Add(MapToStoreSale(p, tienda, itemDutyNorm));
        }

        _logger.LogInformation(
            "Nuevos: {N} | Duplicados: {D} | Sin mapeo: {S} | Excluidos (DF en DP): {E}",
            nuevos.Count, duplicados, sinMapeo, excluidos);

        if (nuevos.Count > 0)
            await _salesRepo.InsertManyAsync(nuevos);

        return nuevos.Count;
    }

    private static StoreSale MapToStoreSale(PosVentaItem p, TiendaSerie tienda, string itemDutyNorm) => new()
    {
        CompanyId          = tienda.CompanyId,
        Transnum           = p.Transnum,
        Itemnum            = p.SaleLineNumber,
        Txnvoidmod         = 0,
        Txnmodifier        = p.EsDevolucion ? 1 : 0,
        Storenum           = p.PosLoc,
        Itemdatetime       = p.BusinessDayDate,
        Skunum             = p.POSItemID,
        Pludesc            = p.Descripcion,
        Qty                = p.Cantidad,
        Stdunitprice       = p.PrecioUnitario,
        Extsellprice       = p.MontoExtendido,
        Extundiscprice     = p.MontoSinImpuesto,
        DeliveryDocNum     = null,
        Numserie           = p.CashDrawerID,
        Numalbaran         = p.SecuenciaTransaccion,
        Abonode_Numalbaran = p.EsVenta ? "-1" : (p.DevNroTicket ?? "-1"),
        Abonode_Numserie   = p.EsVenta ? "0"  : (p.DevWorkstation ?? "0"),
        TransType          = p.TransType,
        CodigoImpuesto     = p.CodigoImpuesto,
        DutyType           = itemDutyNorm,
        SalesTaxAmount     = p.TransactionTaxAmount,
        TicketNo           = $"{p.SiteID}{p.CashDrawerID}{p.NroTicket}"
    };

    /// <summary>
    /// Convierte el DutyType del POS ("DutyFree"/"DutyPaid") al código corto usado en ADR_TIENDA_SERIE ("DF"/"DP").
    /// </summary>
    private static string NormalizeDutyType(string? posItemDutyType) =>
        string.IsNullOrWhiteSpace(posItemDutyType) ? "DP" :
        posItemDutyType.Equals("DutyFree", StringComparison.OrdinalIgnoreCase) ||
        posItemDutyType.Equals("DF", StringComparison.OrdinalIgnoreCase) ? "DF" : "DP";

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 2
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> ProcessPendingAsync(
        Dictionary<string, TiendaSerie> mappings,
        Dictionary<string, List<string>> warehouseLists,
        CancellationToken ct)
    {
        _logger.LogInformation("Fase 2: procesando registros pendientes...");

        var pending = (await _salesRepo.GetPendingAsync()).ToList();
        _logger.LogInformation("Registros pendientes: {Count}", pending.Count);

        if (pending.Count == 0) return 0;

        var grupos = pending.GroupBy(s => s.Transnum).ToList();
        _logger.LogInformation("Transacciones a procesar: {Count}", grupos.Count);

        int docsCreados = 0;
        foreach (var grupo in grupos)
        {
            if (ct.IsCancellationRequested) break;
            if (await ProcessTransactionGroupAsync(grupo.ToList(), mappings, warehouseLists))
                docsCreados++;
        }

        return docsCreados;
    }

    private async Task<bool> ProcessTransactionGroupAsync(
        List<StoreSale> items,
        Dictionary<string, TiendaSerie> mappings,
        Dictionary<string, List<string>> warehouseLists)
    {
        var transnum     = items[0].Transnum;
        var esDevolucion = items[0].EsDevolucion;
        var tipoDoc      = esDevolucion ? "ORDN (Devolución)" : "ODLN (Entrega)";

        var first   = items[0];
        int numStore = int.TryParse(first.Storenum, out var ns) ? ns : 0;

        // Mapeo a nivel de transacción: obtiene CardCode y BPLId
        // (iguales en ambas entradas DF/DP de ADR_TIENDA_SERIE para la misma tienda)
        var txnKey = TiendaSerie.BuildKey(first.Numserie, numStore, first.TransType);
        if (!mappings.TryGetValue(txnKey, out var tienda))
        {
            _logger.LogError(
                "Sin mapeo ADR_TIENDA_SERIE — Serie={S}, Store={St}, DutyType={D}. Txn={Txn}.",
                first.Numserie, first.Storenum, first.TransType, transnum);
            foreach (var item in items)
                await RegistrarError(item, "Sin Mapeo",
                    $"No existe en ADR_TIENDA_SERIE: Serie={first.Numserie}, " +
                    $"Store={first.Storenum}, DutyType={first.TransType}");
            return false;
        }

        // El ítem DEEE solo aporta su monto al freight — se excluye de bodega, stock y líneas
        var deeeCode      = _settings.FreightSettings.DeeeItemCode?.Trim();
        var itemsDocumento = items
            .Where(i => string.IsNullOrEmpty(deeeCode) ||
                        !i.Skunum.Trim().Equals(deeeCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (itemsDocumento.Count == 0)
        {
            _logger.LogWarning(
                "Txn={Txn}: la transacción no tiene ítems de documento (solo ítems DEEE/impuesto). " +
                "Se omite la creación del documento SAP.", transnum);
            foreach (var item in items)
                await _salesRepo.UpdateDeliveryDocNumAsync(item.ID, -2, 0);
            return false;
        }

        // Seleccionar bodega por ítem con verificación de stock integrada
        var whsByItemId = new Dictionary<long, string>();
        var idsConError = new HashSet<long>();
        var skipCodes   = _settings.ProcessingOptions.SkipStockCheckItemCodes
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in itemsDocumento)
        {
            // 1. Override manual tiene prioridad absoluta (no se verifica stock)
            if (!string.IsNullOrWhiteSpace(item.WhsCode))
            {
                _logger.LogDebug(
                    "  SKU={Sku} Txn={Txn} — bodega override: {Whs}",
                    item.Skunum, transnum, item.WhsCode.Trim());
                whsByItemId[item.ID] = item.WhsCode.Trim();
                continue;
            }

            // 2. Obtener lista de bodegas para este ítem
            var itemKey = TiendaSerie.BuildKey(item.Numserie, numStore, item.DutyType);
            if (!warehouseLists.TryGetValue(itemKey, out var whsList) || whsList.Count == 0)
            {
                _logger.LogError(
                    "Sin mapeo ADR_TIENDA_SERIE para ítem — SKU={Sku}, DutyType={D}, Txn={Txn}",
                    item.Skunum, item.DutyType, transnum);
                foreach (var i in items)
                    await RegistrarError(i, "Sin Mapeo",
                        $"No existe ADR_TIENDA_SERIE para ítem SKU={item.Skunum}, DutyType={item.DutyType}");
                return false;
            }

            // 3. Devoluciones: usar primera bodega (no se verifica stock)
            if (esDevolucion)
            {
                whsByItemId[item.ID] = whsList[0];
                await _salesRepo.UpdateWhsCodeAsync(item.ID, whsList[0]);
                continue;
            }

            // 4. Ítems configurados sin validación de stock: primera bodega, se incluyen en el ODLN
            if (skipCodes.Contains(item.Skunum.Trim()))
            {
                whsByItemId[item.ID] = whsList[0];
                await _salesRepo.UpdateWhsCodeAsync(item.ID, whsList[0]);
                _logger.LogDebug(
                    "  SKU={Sku} Txn={Txn} — stock check omitido (SkipStockCheckItemCodes), bodega {Whs}",
                    item.Skunum, transnum, whsList[0]);
                continue;
            }

            // 5. Ventas: buscar primera bodega con stock suficiente
            string? whsConStock = null;
            foreach (var whs in whsList)
            {
                decimal disponible = await _sap.GetAvailableStockAsync(item.Skunum, whs);
                if (disponible >= item.Qty)
                {
                    _logger.LogDebug(
                        "  SKU={Sku} Txn={Txn} — bodega {Whs}: stock OK (disp={D}, req={R})",
                        item.Skunum, transnum, whs, disponible, item.Qty);
                    whsConStock = whs;
                    break;
                }
                _logger.LogDebug(
                    "  SKU={Sku} Txn={Txn} — bodega {Whs}: insuficiente (disp={D}, req={R}){Next}",
                    item.Skunum, transnum, whs, disponible, item.Qty,
                    whsList.Count > 1 ? ", prueba siguiente..." : "");
            }

            if (whsConStock is not null)
            {
                whsByItemId[item.ID] = whsConStock;
                await _salesRepo.UpdateWhsCodeAsync(item.ID, whsConStock);
            }
            else
            {
                // Sin stock en ninguna bodega: registrar error con la primera de la lista
                var fallback = whsList[0];
                _logger.LogWarning(
                    "Stock insuficiente en todas las bodegas — SKU={Sku}, Bodegas={Whs}, Txn={Txn}",
                    item.Skunum, string.Join(",", whsList), transnum);
                await RegistrarError(item, "Insufficient Inventory",
                    $"Sin stock en ninguna bodega: {string.Join(", ", whsList)}");
                idsConError.Add(item.ID);
                whsByItemId[item.ID] = fallback;
            }
        }

        if (idsConError.Count > 0)
            return false;

        _logger.LogDebug(
            "Txn={Txn} | {Tipo} | BPLId={BPL} | CardCode={CC}",
            transnum, tipoDoc, tienda.BPLId, tienda.CardCode);

        // ── Construir documento SAP ───────────────────────────────────────────
        var fechaDoc = items[0].Itemdatetime.ToString("yyyy-MM-dd");

        var uType = items[0].TransType.Equals("DP", StringComparison.OrdinalIgnoreCase)
            ? "Duty Paid"
            : "Duty Free";

        var docRequest = new SapDocumentRequest
        {
            CardCode                = tienda.CardCode,
            BPL_IDAssignedToInvoice = tienda.BPLId,
            DocDate                 = fechaDoc,
            TaxDate                 = fechaDoc,
            DocDueDate              = fechaDoc,
            U_Type                  = uType,
            Comments                = $"Automatically generated by POS integration process on {DateTime.Now:yyyy-MM-dd HH:mm:ss}.",
            U_BOL                   = items[0].Numserie,
            NumAtCard               = items[0].TicketNo,

            // Precio NETO (sin impuesto) — cada línea lleva su bodega según DutyType del ítem.
            // itemsDocumento ya excluye el ítem DEEE.
            DocumentLines = itemsDocumento.Select(i => new SapDocumentLine
            {
                ItemCode      = i.Skunum,
                Quantity      = Math.Abs(i.Qty),
                UnitPrice     = Math.Abs(i.Qty) > 0
                                    ? Math.Round(Math.Abs(i.Extsellprice) / Math.Abs(i.Qty), 6)
                                    : i.Stdunitprice,
                WarehouseCode = whsByItemId[i.ID]
            }).ToList(),

            // Impuestos como freight por código de impuesto
            DocumentAdditionalExpenses = BuildFreightEntries(items)
        };

        // Omitir la lista si está vacía (SAP no acepta lista vacía en algunos casos)
        if (docRequest.DocumentAdditionalExpenses?.Count == 0)
            docRequest.DocumentAdditionalExpenses = null;

        // ── Crear en SAP ─────────────────────────────────────────────────────
        try
        {
            var (docNum, docEntry) = esDevolucion
                ? await _sap.CreateReturnAsync(docRequest)
                : await _sap.CreateDeliveryNoteAsync(docRequest);

            var bodegas = string.Join(",", whsByItemId.Values.Distinct());
            _logger.LogInformation(
                "Doc SAP creado: {Tipo} DocNum={DocNum} DocEntry={DocEntry} | BPLId={BPL} | Txn={Txn} | " +
                "{N} líneas | Bodegas={Whs} | Freight: {F}",
                tipoDoc, docNum, docEntry, tienda.BPLId, transnum, items.Count,
                bodegas, docRequest.DocumentAdditionalExpenses?.Count ?? 0);

            foreach (var item in items)
            {
                await _salesRepo.UpdateDeliveryDocNumAsync(item.ID, docNum, docEntry);
                await _salesRepo.DeleteErrorAsync(item.Transnum, item.Itemnum);
            }

            return true;
        }
        catch (SapException ex)
        {
            _logger.LogError(
                "Error SAP creando {Tipo} para Txn={Txn}: {Error}",
                tipoDoc, transnum, ex.Message);
            foreach (var item in items)
                await RegistrarError(item, "SAP Error", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Genera las entradas de freight (DocumentAdditionalExpenses) para el ODLN/ORDN.
    /// ExpnsCode=1 (Sales Tax): monto exacto de la transacción (TransactionGrandAmount - TransactionNetAmount).
    /// ExpnsCode=2 (Tax DEEE): MontoExtendido del ítem con POSItemID = DeeeItemCode.
    /// </summary>
    private List<SapAdditionalExpense> BuildFreightEntries(List<StoreSale> items)
    {
        var result  = new List<SapAdditionalExpense>();
        var freight = _settings.FreightSettings;

        // ExpnsCode=1: Sales Tax exacto de la transacción
        decimal salesTax = Math.Abs(items[0].SalesTaxAmount);
        if (salesTax > 0)
        {
            result.Add(new SapAdditionalExpense
            {
                ExpenseCode        = freight.SalesTaxExpnsCode,
                LineTotal          = Math.Round(salesTax, 2),
                DistributionMethod = "dNone"
            });
        }

        // ExpnsCode=2: Tax DEEE — MontoExtendido del ítem específico (DeeeItemCode)
        if (!string.IsNullOrEmpty(freight.DeeeItemCode))
        {
            var deeeCode = freight.DeeeItemCode.Trim();
            decimal deeeTax = Math.Abs(items
                .Where(i => i.Skunum.Trim().Equals(deeeCode, StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.Extsellprice));

            if (deeeTax > 0)
            {
                result.Add(new SapAdditionalExpense
                {
                    ExpenseCode        = freight.DeeeTaxExpnsCode,
                    LineTotal          = Math.Round(deeeTax, 2),
                    DistributionMethod = "dNone"
                });
            }
        }

        return result;
    }

    private async Task RegistrarError(StoreSale item, string tipo, string detalle)
    {
        await _salesRepo.MarkAsErrorAsync(item.ID);

        // El ítem DEEE (impuesto) se marca como error para que se reintente,
        // pero NO se inserta en la_delivery_errors para no bloquear la facturación.
        var deeeCode = _settings.FreightSettings.DeeeItemCode?.Trim();
        if (!string.IsNullOrEmpty(deeeCode) &&
            item.Skunum.Trim().Equals(deeeCode, StringComparison.OrdinalIgnoreCase))
            return;

        // Los ítems configurados sin validación de stock tampoco bloquean la_delivery_errors.
        var skipCodes = _settings.ProcessingOptions.SkipStockCheckItemCodes;
        if (skipCodes.Any(c => c.Trim().Equals(item.Skunum.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;

        await _salesRepo.UpsertErrorAsync(new DeliveryError
        {
            CompanyId      = item.CompanyId,
            Transnum       = item.Transnum,
            Itemnum        = item.Itemnum,
            Txnvoidmod     = item.Txnvoidmod,
            Txnmodifier    = item.Txnmodifier,
            Storenum       = item.Storenum,
            Itemdatetime   = item.Itemdatetime,
            Skunum         = item.Skunum,
            Pludesc        = item.Pludesc,
            Qty            = item.Qty,
            Stdunitprice   = item.Stdunitprice,
            Extsellprice   = item.Extsellprice,
            Extundiscprice = item.Extundiscprice,
            DeliveryDocNum = null,
            DateCreated    = DateTime.Now,
            ErrorMessage   = $"[{tipo}] {detalle}"
        });
    }
}
