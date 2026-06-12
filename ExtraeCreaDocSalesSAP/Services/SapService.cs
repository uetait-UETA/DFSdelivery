using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExtraeCreaDocSalesSAP.Configuration;
using ExtraeCreaDocSalesSAP.Models.Sap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExtraeCreaDocSalesSAP.Services;

public interface ISapService
{
    Task<bool> LoginAsync();
    Task LogoutAsync();
    Task<decimal> GetAvailableStockAsync(string itemCode, string warehouseCode);
    Task<(int DocNum, int DocEntry)> CreateDeliveryNoteAsync(SapDocumentRequest request);
    Task<(int DocNum, int DocEntry)> CreateReturnAsync(SapDocumentRequest request);

    /// <summary>
    /// Busca el DocEntry en SAP dado un DocNum.
    /// esDevolucion=true consulta Returns; false consulta DeliveryNotes.
    /// Retorna null si no lo encuentra.
    /// </summary>
    Task<int?> GetDocEntryByDocNumAsync(int docNum, bool esDevolucion);
}

public class SapService : ISapService, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly SapServiceLayerSettings _cfg;
    private readonly ILogger<SapService> _logger;

    private string? _sessionCookie;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Opciones para serializar requests a SAP: ignora nulls para no enviar campos opcionales vacíos
    private static readonly JsonSerializerOptions JsonSendOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SapService(HttpClient http, IOptions<AppSettings> settings, ILogger<SapService> logger)
    {
        _http   = http;
        _cfg    = settings.Value.SapServiceLayer;
        _logger = logger;
    }

    // ── Autenticación ─────────────────────────────────────────────────────────

    public async Task<bool> LoginAsync()
    {
        var payload = new SapLoginRequest
        {
            CompanyDB = _cfg.CompanyDB,
            UserName  = _cfg.UserName,
            Password  = _cfg.Password
        };

        try
        {
            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("Login", content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("SAP Login falló [{Status}]: {Body}", response.StatusCode, body);
                return false;
            }

            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var session = cookies.FirstOrDefault(c => c.StartsWith("B1SESSION="));
                if (session is not null)
                {
                    _sessionCookie = session.Split(';')[0]; // "B1SESSION=xxxxxxxx"
                    _logger.LogInformation("SAP Login exitoso — empresa: {CompanyDB}", _cfg.CompanyDB);
                    return true;
                }
            }

            _logger.LogWarning("SAP Login OK pero sin cookie B1SESSION en la respuesta");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando con SAP Service Layer ({Url})", _cfg.BaseUrl);
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        if (_sessionCookie is null) return;
        try
        {
            using var req = BuildRequest(HttpMethod.Post, "Logout");
            await _http.SendAsync(req);
            _logger.LogDebug("SAP Logout exitoso");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error en SAP Logout");
        }
        finally
        {
            _sessionCookie = null;
        }
    }

    // ── Inventario ────────────────────────────────────────────────────────────

    public async Task<decimal> GetAvailableStockAsync(string itemCode, string warehouseCode)
    {
        // SAP B1 SL no soporta $filter en navigation properties de Items
        // Traemos todas las bodegas del ítem y filtramos en memoria
        var url = $"Items('{Uri.EscapeDataString(itemCode)}')/ItemWarehouseInfoCollection";

        for (int attempt = 1; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = BuildRequest(HttpMethod.Get, url);
                var response = await _http.SendAsync(req);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Sesión SAP expirada consultando stock de {Item}. Reintentando login.", itemCode);
                    _sessionCookie = null;
                    if (!await LoginAsync()) return 0;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    var sapMsg  = TryParseError(errBody) ?? errBody;
                    _logger.LogError("SAP stock query falló [{Status}] SKU={Item} Whs={Whs}: {Msg}",
                        response.StatusCode, itemCode, warehouseCode, sapMsg);
                    return 0;
                }

                var result = await response.Content
                    .ReadFromJsonAsync<SapItemStockResponse>(JsonOpts);

                var info = result?.ItemWarehouseInfoCollection?.FirstOrDefault(w =>
                    string.Equals(w.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase));

                if (info is null)
                {
                    _logger.LogWarning("Item {Item} no encontrado en almacén {Whs}", itemCode, warehouseCode);
                    return 0;
                }

                return info.Available;
            }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error consultando stock {Item}, intento {A}/{M}",
                    itemCode, attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error definitivo consultando stock {Item}", itemCode);
                return 0;
            }
        }

        return 0;
    }

    // ── Documentos ────────────────────────────────────────────────────────────

    public Task<(int DocNum, int DocEntry)> CreateDeliveryNoteAsync(SapDocumentRequest request) =>
        PostDocumentAsync("DeliveryNotes", request);

    public Task<(int DocNum, int DocEntry)> CreateReturnAsync(SapDocumentRequest request) =>
        PostDocumentAsync("Returns", request);

    private async Task<(int DocNum, int DocEntry)> PostDocumentAsync(string endpoint, SapDocumentRequest request)
    {
        var jsonBody = JsonSerializer.Serialize(request, JsonSendOpts);
        _logger.LogDebug("POST {Endpoint}: {Body}", endpoint, jsonBody);

        for (int attempt = 1; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = BuildRequest(HttpMethod.Post, endpoint);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);

                if (!response.IsSuccessStatusCode)
                {
                    var body  = await response.Content.ReadAsStringAsync();
                    var texto = TryParseError(body) ?? body;
                    throw new SapException(texto, (int)response.StatusCode);
                }

                var doc = await response.Content.ReadFromJsonAsync<SapDocumentResponse>(JsonOpts);
                if (doc is null) throw new SapException("SAP no retornó respuesta", 0);
                return (doc.DocNum, doc.DocEntry);
            }
            catch (SapException) { throw; }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error en {Endpoint}, intento {A}/{M}",
                    endpoint, attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        throw new SapException(
            $"No se pudo crear {endpoint} tras {_cfg.MaxRetries} intentos", 0);
    }

    // ── Consulta DocEntry por DocNum ──────────────────────────────────────────

    public async Task<int?> GetDocEntryByDocNumAsync(int docNum, bool esDevolucion)
    {
        var endpoint = esDevolucion ? "Returns" : "DeliveryNotes";
        var url      = $"{endpoint}?$filter=DocNum eq {docNum}&$select=DocNum,DocEntry";

        for (int attempt = 1; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = BuildRequest(HttpMethod.Get, url);
                var response  = await _http.SendAsync(req);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _sessionCookie = null;
                    if (!await LoginAsync()) return null;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Error consultando {EP} DocNum={N}: {Body}",
                        endpoint, docNum, body);
                    return null;
                }

                var list = await response.Content
                    .ReadFromJsonAsync<SapODataList<SapDocumentResponse>>(JsonOpts);

                return list?.Value?.FirstOrDefault()?.DocEntry;
            }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error GET {EP} DocNum={N}, intento {A}/{M}",
                    endpoint, docNum, attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error definitivo GET {EP} DocNum={N}", endpoint, docNum);
                return null;
            }
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (_sessionCookie is not null)
            req.Headers.Add("Cookie", _sessionCookie);
        return req;
    }

    private static string? TryParseError(string json)
    {
        try
        {
            var err = JsonSerializer.Deserialize<SapErrorResponse>(json, JsonOpts);
            return err?.Error?.Message?.Value;
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync() => await LogoutAsync();
}

public class SapException : Exception
{
    public int StatusCode { get; }
    public SapException(string message, int statusCode) : base(message)
        => StatusCode = statusCode;
}
