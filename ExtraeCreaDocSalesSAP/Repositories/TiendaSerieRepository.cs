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
    /// </summary>
    Task<Dictionary<string, TiendaSerie>> GetAllMappingsAsync();
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

        var dict = rows.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

        _logger.LogDebug("ADR_TIENDA_SERIE cargada: {Count} registros", dict.Count);
        return dict;
    }
}
