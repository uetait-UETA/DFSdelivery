using Dapper;
using ExtraeCreaDocSalesSAP.Data;
using ExtraeCreaDocSalesSAP.Models;
using Microsoft.Extensions.Logging;

namespace ExtraeCreaDocSalesSAP.Repositories;

public interface ITiendaSerieRepository
{
    /// <summary>
    /// Carga todos los registros de ADR_TIENDA_SERIE en un diccionario
    /// indexado por "NUMSERIE|NUMSTORE|DUTYTYPE" para búsqueda O(1).
    /// Cuando hay filas duplicadas por clave, retiene la primera y loguea advertencia.
    /// </summary>
    Task<Dictionary<string, TiendaSerie>> GetAllMappingsAsync();

    /// <summary>
    /// Retorna todas las bodegas disponibles por clave "NUMSERIE|NUMSTORE|DUTYTYPE",
    /// ordenadas por WHSCODE ASC. Permite seleccionar la primera con stock suficiente.
    /// </summary>
    Task<Dictionary<string, List<string>>> GetWarehouseListsAsync();
}

public class TiendaSerieRepository : ITiendaSerieRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<TiendaSerieRepository> _logger;

    public TiendaSerieRepository(IDbConnectionFactory factory, ILogger<TiendaSerieRepository> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public async Task<Dictionary<string, TiendaSerie>> GetAllMappingsAsync()
    {
        const string sql = """
            SELECT
                WHSCODE  AS WhsCode,
                CARCODE  AS CardCode,
                NUMSERIE AS NumSerie,
                NUMSTORE AS NumStore,
                CompanyId,
                DUTYTYPE AS DutyType
            FROM [dbo].[ADR_TIENDA_SERIE]
            """;

        await using var conn = _factory.CreateInternal();
        var rows = await conn.QueryAsync<TiendaSerie>(sql);

        var dict = new Dictionary<string, TiendaSerie>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!dict.TryAdd(row.Key, row))
                _logger.LogWarning(
                    "ADR_TIENDA_SERIE: clave duplicada ignorada — {Key} (WHSCODE={Whs}, CARCODE={CC}). " +
                    "Revisa y elimina la fila duplicada.",
                    row.Key, row.WhsCode, row.CardCode);
        }

        _logger.LogDebug("ADR_TIENDA_SERIE cargada: {Count} registros", dict.Count);
        return dict;
    }

    public async Task<Dictionary<string, List<string>>> GetWarehouseListsAsync()
    {
        const string sql = """
            SELECT
                WHSCODE  AS WhsCode,
                NUMSERIE AS NumSerie,
                NUMSTORE AS NumStore,
                DUTYTYPE AS DutyType
            FROM [dbo].[ADR_TIENDA_SERIE]
            ORDER BY NUMSERIE, NUMSTORE, DUTYTYPE, WHSCODE
            """;

        await using var conn = _factory.CreateInternal();
        var rows = await conn.QueryAsync<TiendaSerie>(sql);

        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!dict.TryGetValue(row.Key, out var list))
                dict[row.Key] = list = new List<string>();
            var whs = row.WhsCode.Trim();
            if (!list.Contains(whs, StringComparer.OrdinalIgnoreCase))
                list.Add(whs);
        }

        _logger.LogDebug("ADR_TIENDA_SERIE bodegas por clave: {Count} claves únicas", dict.Count);
        return dict;
    }
}
