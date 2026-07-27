namespace Dataverse.Core.Clients;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Thin wrapper around HttpClient for Dataverse Web API calls.
/// Handles auth headers, OData headers, and Polly retry on 429/503.
/// </summary>
public sealed class DataverseHttpClient
{
    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<DataverseHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializer for Web API action/function payloads. Dataverse action parameters are
    /// case-sensitive and PascalCase (e.g. <c>SolutionName</c>, <c>CustomizationFile</c>,
    /// <c>ComponentId</c>) — the camelCase policy used for entity bodies would break them.
    /// </summary>
    private static readonly JsonSerializerOptions ActionJsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    public DataverseHttpClient(
        HttpClient http,
        ITokenProvider tokenProvider,
        ILogger<DataverseHttpClient> logger)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string orgUrl, string relativeUrl, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, orgUrl, relativeUrl, body: null, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task<byte[]> GetBytesAsync(string orgUrl, string relativeUrl, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, orgUrl, relativeUrl, body: null, ct);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> GetRawAsync(
        string orgUrl,
        string relativeUrl,
        bool includeFormattedValues = false,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, orgUrl, relativeUrl, body: null, ct);
        if (includeFormattedValues)
            request.Headers.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<T?> PostAsync<T>(string orgUrl, string relativeUrl, object body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task PostAsync(string orgUrl, string relativeUrl, object body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>POST returning the raw response body as a string (or empty on 204). Sets
    /// <c>Prefer: return=representation</c> by default so Dataverse returns the created row
    /// in the body — otherwise many endpoints respond with 204 No Content.</summary>
    public async Task<string> PostRawAsync(
        string orgUrl,
        string relativeUrl,
        object body,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        bool requestRepresentation = true,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, body, ct);
        if (requestRepresentation)
            request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        if (extraHeaders != null)
            foreach (var (k, v) in extraHeaders)
                request.Headers.TryAddWithoutValidation(k, v);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return string.Empty;
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST returning the GUID of the newly created row. Dataverse reports it in the
    /// <c>OData-EntityId</c> response header (e.g. <c>https://org.crm4.dynamics.com/api/data/v9.2/savedqueries(guid)</c>);
    /// when that header is missing the value is read from <paramref name="idPropertyName"/> in the
    /// response body. Returns <see cref="Guid.Empty"/> when neither carries an id.</summary>
    public async Task<Guid> PostForIdAsync(
        string orgUrl,
        string relativeUrl,
        object body,
        string idPropertyName,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        if (response.Headers.TryGetValues("OData-EntityId", out var headerValues))
        {
            var id = ParseEntityIdHeader(headerValues.FirstOrDefault());
            if (id != Guid.Empty)
                return id;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Guid.Empty;

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return Guid.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(idPropertyName, out var idEl) &&
                idEl.ValueKind == JsonValueKind.String &&
                Guid.TryParse(idEl.GetString(), out var bodyId))
            {
                return bodyId;
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — no id to recover, fall through.
        }

        return Guid.Empty;
    }

    /// <summary>Extract the GUID from an <c>OData-EntityId</c> header value ("…/collection(guid)").</summary>
    private static Guid ParseEntityIdHeader(string? entityUri)
    {
        if (string.IsNullOrWhiteSpace(entityUri))
            return Guid.Empty;

        var open = entityUri.LastIndexOf('(');
        var close = entityUri.LastIndexOf(')');
        if (open < 0 || close < open)
            return Guid.Empty;

        return Guid.TryParse(entityUri.Substring(open + 1, close - open - 1), out var id) ? id : Guid.Empty;
    }

    public async Task PatchAsync(string orgUrl, string relativeUrl, object body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Patch, orgUrl, relativeUrl, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task PatchAsync(
        string orgUrl,
        string relativeUrl,
        object body,
        IReadOnlyDictionary<string, string> extraHeaders,
        CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Patch, orgUrl, relativeUrl, body, ct);
        foreach (var (k, v) in extraHeaders)
            request.Headers.TryAddWithoutValidation(k, v);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T?> ExecuteActionAsync<T>(
        string orgUrl,
        string actionName,
        object parameters,
        CancellationToken ct = default)
    {
        var relativeUrl = $"api/data/v9.2/{actionName}";
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, parameters, ct, ActionJsonOptions);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task ExecuteActionAsync(
        string orgUrl,
        string actionName,
        object parameters,
        CancellationToken ct = default)
    {
        var relativeUrl = $"api/data/v9.2/{actionName}";
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, parameters, ct, ActionJsonOptions);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string orgUrl,
        string relativeUrl,
        object? body,
        CancellationToken ct,
        JsonSerializerOptions? bodySerializerOptions = null)
    {
        var scope = $"{orgUrl.TrimEnd('/')}/.default";
        var token = await _tokenProvider.GetTokenAsync(scope, ct);

        var uri = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(relativeUrl)
            : new Uri($"{orgUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}");

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, bodySerializerOptions ?? JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Dataverse API error {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Dataverse API error {(int)response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }
}
