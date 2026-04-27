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

    public async Task<string> GetRawAsync(string orgUrl, string relativeUrl, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, orgUrl, relativeUrl, body: null, ct);
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

    public async Task PatchAsync(string orgUrl, string relativeUrl, object body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Patch, orgUrl, relativeUrl, body, ct);
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
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, parameters, ct);
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
        using var request = await BuildRequestAsync(HttpMethod.Post, orgUrl, relativeUrl, parameters, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string orgUrl,
        string relativeUrl,
        object? body,
        CancellationToken ct)
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
            var json = JsonSerializer.Serialize(body, JsonOptions);
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
