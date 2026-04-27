namespace Dataverse.Core.Clients;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Auth;
using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP client for the Power Automate Flow API and Business Application Platform (BAP) API.
/// Base URL is region-specific: https://{region}.api.flow.microsoft.com
/// BAP (environments, pipelines): https://api.bap.microsoft.com
/// </summary>
public sealed class PowerAutomateHttpClient
{
    private const string FlowScope = "https://service.flow.microsoft.com/.default";
    private const string BapBaseUrl = "https://api.bap.microsoft.com";

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<PowerAutomateHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PowerAutomateHttpClient(
        HttpClient http,
        ITokenProvider tokenProvider,
        ILogger<PowerAutomateHttpClient> logger)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string url, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, url, null, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task<string> GetRawAsync(string url, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Get, url, null, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<T?> PostAsync<T>(string url, object? body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, url, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task PostAsync(string url, object? body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, url, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T?> PatchAsync<T>(string url, object body, CancellationToken ct = default)
    {
        using var request = await BuildRequestAsync(HttpMethod.Patch, url, body, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>Builds the Flow API base URL from region.</summary>
    public static string FlowApiBase(string region) =>
        $"https://{region}.api.flow.microsoft.com";

    /// <summary>Builds the flows collection URL for an environment.</summary>
    public static string FlowsUrl(string region, string environmentId) =>
        $"{FlowApiBase(region)}/providers/Microsoft.ProcessSimple/environments/{environmentId}/flows";

    public static string BapEnvironmentsUrl() =>
        $"{BapBaseUrl}/providers/Microsoft.BusinessAppPlatform/environments?api-version=2016-11-01";

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(FlowScope, ct);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
            _logger.LogError("Power Automate API error {StatusCode}: {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
    }
}
