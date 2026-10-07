namespace Dataverse.Core.Safety;

using System.Text.Json;
using Dataverse.Core.Clients;
using Microsoft.Extensions.Logging;

/// <summary>
/// Looks up the organization type of a Dataverse environment via RetrieveCurrentOrganization.
/// Uses the Dataverse token the server already holds — the BAP environments listing would need a
/// second token, and for an app registration without Power Automate consent that means a browser
/// prompt on the first write. The result is cached per org URL for the lifetime of the server
/// process; a failed lookup is not cached, so the next write retries.
/// </summary>
public sealed class EnvironmentTypeService
{
    private const string RetrieveCurrentOrganizationUrl =
        "RetrieveCurrentOrganization(AccessType=@p1)?@p1=Microsoft.Dynamics.CRM.EndpointAccessType'Default'";

    private readonly DataverseHttpClient _client;
    private readonly ILogger<EnvironmentTypeService> _logger;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EnvironmentTypeService(DataverseHttpClient client, ILogger<EnvironmentTypeService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Production organizations (<c>Customer</c> = primary, <c>Secondary</c> = further production
    /// instances) and the tenant's <c>Default</c> environment, which every licensed user can reach.
    /// Sandbox (<c>CustomerTest</c>), <c>Developer</c>, <c>Trial</c> and the rest are not protected.
    /// See Microsoft.Xrm.Sdk.Organization.OrganizationType.
    /// </summary>
    public static bool IsProtected(string? organizationType) =>
        organizationType is not null &&
        (organizationType.Equals("Customer", StringComparison.OrdinalIgnoreCase) ||
         organizationType.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
         organizationType.Equals("Default", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the organization type, or null when it cannot be read (missing privilege, network) —
    /// the caller decides what null means.
    /// </summary>
    public async Task<string?> GetOrganizationTypeAsync(string orgUrl, CancellationToken ct = default)
    {
        var key = orgUrl.Trim().TrimEnd('/');
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var response = await _client.GetAsync<JsonElement>(key, RetrieveCurrentOrganizationUrl, ct);
                var type = ReadOrganizationType(response);
                _cache[key] = type;
                return type;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not determine the organization type of {OrgUrl}.", orgUrl);
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Reads Detail.OrganizationType from a RetrieveCurrentOrganization response.</summary>
    public static string? ReadOrganizationType(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("Detail", out var detail) ||
            detail.ValueKind != JsonValueKind.Object ||
            !detail.TryGetProperty("OrganizationType", out var type))
            return null;

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            // Defensive: should the enum ever arrive as its number, map the protected ones.
            JsonValueKind.Number => type.GetInt32() switch { 0 => "Customer", 4 => "Secondary", 12 => "Default", var n => n.ToString() },
            _ => null,
        };
    }
}
