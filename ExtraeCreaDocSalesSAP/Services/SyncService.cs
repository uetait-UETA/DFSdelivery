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

        int insertados = await ExtractAndStoreAsync(mappings, ct);

        int procesados = 0;
        bool loggedIn  = await _sap.LoginAsync();
        if (!loggedIn)
        {
            _logger.LogError("No se pudo autenticar en SAP B1. Se omite la Fase 2.");
        }
        else
        {
            try   { procesados = await ProcessPendingAsync(mappings, ct); }
            finally { await _sap.LogoutAsync(); }
        }

        _logger.LogInformation(
            "Proceso finalizado — Insertados: {I} | Documentos SAP creados: {P}",
            insertados, procesados);
        _logger.LogInformation("═══════════════════════════════════════════════════════");

        return procesados;
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
            var deeeItemCode = _settings.FreightSettings.DeeeItemCode;
            bool esItemDeee  = !string.IsNullOrEmpty(deeeItemCode) &&
                               p.POSItemID.Equals(deeeItemCode, StringComparison.OrdinalIgnoreCase);

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
        SalesTaxAmount     = p.TransactionTaxAmount
    };

    /// <summary>
    /// Convierte el DutyType del POS ("DutyFree"/"DutyPaid") al código corto usado en ADR_TIENDA_SERIE ("DF"/"DP").
    /// </summary>
    private static string NormalizeDutyType(string posItemDutyType) =>
        posItemDutyType.Equals("DutyFree", StringComparison.OrdinalIgnoreCase) ? "DF" : "DP";

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 2
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<int> ProcessPendingAsync(
        Dictionary<string, TiendaSerie> mappings, CancellationToken ct)
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
            if (await ProcessTransactionGroupAsync(grupo.ToList(), mappings))
                docsCreados++;
        }

        return docsCreados;
    }

    private async Task<bool> ProcessTransactionGroupAsync(
        List<StoreSale> items, Dictionary<string, TiendaSerie> mappings)
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
        var deeeCode      = _settings.FreightSettings.DeeeItemCode;
        var itemsDocumento = items
            .Where(i => string.IsNullOrEmpty(deeeCode) ||
                        !i.Skunum.Equals(deeeCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pre-calcular bodega por ítem según su DutyType (solo ítems de documento)
        var whsByItemId = new Dictionary<long, string>();
        foreach (var item in itemsDocumento)
        {
            var itemKey = TiendaSerie.BuildKey(item.Numserie, numStore, item.DutyType);
            if (!mappings.TryGetValue(itemKey, out var itemTienda))
            {
                _logger.LogError(
                    "Sin mapeo ADR_TIENDA_SERIE para ítem — SKU={Sku}, DutyType={D}, Txn={Txn}",
                    item.Skunum, item.DutyType, transnum);
                foreach (var i in items)
                    await RegistrarError(i, "Sin Mapeo",
                        $"No existe ADR_TIENDA_SERIE para ítem SKU={item.Skunum}, DutyType={item.DutyType}");
                return false;
            }
            whsByItemId[item.ID] = itemTienda.WhsCode;
        }

        _logger.LogDebug(
            "Txn={Txn} | {Tipo} | BPLId={BPL} | CardCode={CC}",
            transnum, tipoDoc, tienda.BPLId, tienda.CardCode);

        // ── Validar inventario (solo ventas, solo ítems de documento) ─────────
        if (!esDevolucion)
        {
            var idsConError = new HashSet<long>();
            foreach (var item in itemsDocumento)
            {
                var whsCode        = whsByItemId[item.ID];
                decimal disponible = await _sap.GetAvailableStockAsync(item.Skunum, whsCode);
                if (disponible < item.Qty)
                {
                    _logger.LogWarning(
                        "Stock insuficiente — SKU={Sku}, Whs={Whs}, Disp={D}, Req={R}, Txn={Txn}",
                        item.Skunum, whsCode, disponible, item.Qty, transnum);
                    await RegistrarError(item, "Insufficient Inventory",
                        $"Stock disponible: {disponible}, requerido: {item.Qty}, bodega: {whsCode}");
                    idsConError.Add(item.ID);
                }
            }

            if (idsConError.Count > 0)
                return false;
        }

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
            NumAtCard               = items[0].Numalbaran.ToString(),

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
            decimal deeeTax = Math.Abs(items
                .Where(i => i.Skunum.Equals(freight.DeeeItemCode, StringComparison.OrdinalIgnoreCase))
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
