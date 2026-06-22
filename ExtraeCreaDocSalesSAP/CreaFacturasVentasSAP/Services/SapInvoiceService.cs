using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CreaFacturasVentasSAP.Configuration;
using CreaFacturasVentasSAP.Models.Sap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreaFacturasVentasSAP.Services;

public interface ISapInvoiceService
{
    Task<bool> LoginAsync();
    Task LogoutAsync();

    /// <summary>Obtiene líneas y freights de un DeliveryNote (ODLN) por su DocEntry.</summary>
    Task<SapDocumentData> GetDeliveryLinesAsync(int docEntry);

    /// <summary>Obtiene líneas y freights de un Return (ORDN) por su DocEntry.</summary>
    Task<SapDocumentData> GetReturnLinesAsync(int docEntry);

    /// <summary>Crea una factura OINV. Retorna (DocNum, DocEntry).</summary>
    Task<(int DocNum, int DocEntry)> CreateInvoiceAsync(SapInvoiceRequest request);

    /// <summary>Crea una nota de crédito ORIN. Retorna (DocNum, DocEntry).</summary>
    Task<(int DocNum, int DocEntry)> CreateCreditNoteAsync(SapInvoiceRequest request);

    /// <summary>Crea un Incoming Payment ORCT. Retorna DocNum.</summary>
    Task<int> CreateIncomingPaymentAsync(SapIncomingPaymentRequest request);
}

public class SapInvoiceService : ISapInvoiceService, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly SapServiceLayerSettings _cfg;
    private readonly ILogger<SapInvoiceService> _logger;

    private string? _sessionCookie;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonSendOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SapInvoiceService(
        HttpClient http,
        IOptions<AppSettings> settings,
        ILogger<SapInvoiceService> logger)
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
            var json     = JsonSerializer.Serialize(payload);
            var content  = new StringContent(json, Encoding.UTF8, "application/json");
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
                    _sessionCookie = session.Split(';')[0];
                    _logger.LogInformation("SAP Login exitoso — empresa: {CompanyDB}", _cfg.CompanyDB);
                    return true;
                }
            }

            _logger.LogWarning("SAP Login OK pero sin cookie B1SESSION");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando SAP Service Layer ({Url})", _cfg.BaseUrl);
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

    // ── Consulta líneas de ODLN / ORDN ───────────────────────────────────────

    public Task<SapDocumentData> GetDeliveryLinesAsync(int docEntry) =>
        GetDocumentDataAsync("DeliveryNotes", docEntry);

    public Task<SapDocumentData> GetReturnLinesAsync(int docEntry) =>
        GetDocumentDataAsync("Returns", docEntry);

    private async Task<SapDocumentData> GetDocumentDataAsync(string sapEntity, int docEntry)
    {
        var url = $"{sapEntity}({docEntry})?$select=DocEntry,DocNum,DocumentLines,DocumentAdditionalExpenses";

        for (int attempt = 1; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = BuildRequest(HttpMethod.Get, url);
                var response  = await _http.SendAsync(req);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _sessionCookie = null;
                    if (!await LoginAsync()) return new SapDocumentData();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Error consultando {Entity} DocEntry={DE}: {Body}",
                        sapEntity, docEntry, body);
                    return new SapDocumentData();
                }

                var doc = await response.Content.ReadFromJsonAsync<SapDeliveryNoteResponse>(JsonOpts);
                return new SapDocumentData
                {
                    Lines    = doc?.DocumentLines    ?? [],
                    Expenses = doc?.DocumentAdditionalExpenses ?? []
                };
            }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error GET {Entity} {DE}, intento {A}/{M}",
                    sapEntity, docEntry, attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error definitivo GET {Entity} {DE}", sapEntity, docEntry);
                return new SapDocumentData();
            }
        }

        return new SapDocumentData();
    }

    // ── Documentos ────────────────────────────────────────────────────────────

    public Task<(int DocNum, int DocEntry)> CreateInvoiceAsync(SapInvoiceRequest request) =>
        PostDocumentAsync("Invoices", request);

    public Task<(int DocNum, int DocEntry)> CreateCreditNoteAsync(SapInvoiceRequest request) =>
        PostDocumentAsync("CreditNotes", request);

    private async Task<(int DocNum, int DocEntry)> PostDocumentAsync(
        string endpoint, SapInvoiceRequest request)
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
                    throw new SapInvoiceException(texto, (int)response.StatusCode);
                }

                var doc = await response.Content.ReadFromJsonAsync<SapDocumentResponse>(JsonOpts);
                if (doc is null) throw new SapInvoiceException("SAP no retornó respuesta", 0);
                return (doc.DocNum, doc.DocEntry);
            }
            catch (SapInvoiceException) { throw; }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error en {Endpoint}, intento {A}/{M}",
                    endpoint, attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        throw new SapInvoiceException(
            $"No se pudo crear {endpoint} tras {_cfg.MaxRetries} intentos", 0);
    }

    // ── Incoming Payment ──────────────────────────────────────────────────────

    public async Task<int> CreateIncomingPaymentAsync(SapIncomingPaymentRequest request)
    {
        var jsonBody = JsonSerializer.Serialize(request, JsonSendOpts);
        _logger.LogDebug("POST IncomingPayments: {Body}", jsonBody);

        for (int attempt = 1; attempt <= _cfg.MaxRetries; attempt++)
        {
            try
            {
                using var req = BuildRequest(HttpMethod.Post, "IncomingPayments");
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);

                if (!response.IsSuccessStatusCode)
                {
                    var body  = await response.Content.ReadAsStringAsync();
                    var texto = TryParseError(body) ?? body;
                    throw new SapInvoiceException(texto, (int)response.StatusCode);
                }

                var doc = await response.Content.ReadFromJsonAsync<SapDocumentResponse>(JsonOpts);
                if (doc is null) throw new SapInvoiceException("SAP no retornó respuesta del cobro", 0);
                return doc.DocNum;
            }
            catch (SapInvoiceException) { throw; }
            catch (Exception ex) when (attempt < _cfg.MaxRetries)
            {
                _logger.LogWarning(ex, "Error creando IncomingPayment, intento {A}/{M}",
                    attempt, _cfg.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }

        throw new SapInvoiceException(
            $"No se pudo crear IncomingPayment tras {_cfg.MaxRetries} intentos", 0);
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

public class SapInvoiceException : Exception
{
    public int StatusCode { get; }
    public SapInvoiceException(string message, int statusCode) : base(message)
        => StatusCode = statusCode;
}
