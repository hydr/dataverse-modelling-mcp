namespace Dataverse.Core.Services;

using System.Text.Json;
using System.Xml.Linq;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class ViewService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<ViewService> _logger;

    public ViewService(DataverseHttpClient client, ILogger<ViewService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ViewSummary>> ListAsync(
        string orgUrl,
        string tableLogicalName,
        string? viewType = null,
        CancellationToken ct = default)
    {
        var filter = $"returnedtypecode eq '{tableLogicalName}'";
        if (!string.IsNullOrWhiteSpace(viewType))
            filter += $" and querytype eq {MapViewType(viewType)}";

        var url = $"api/data/v9.2/savedqueries" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=savedqueryid,name,querytype,isdefault,iscustomizable";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<ViewSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new ViewSummary(
                    ViewId: item.TryGetGuid("savedqueryid"),
                    Name: item.GetStringOrEmpty("name"),
                    ViewType: MapQueryTypeToName(item.GetInt32OrZero("querytype")),
                    IsDefault: item.TryGetProperty("isdefault", out var def) && def.ValueKind == JsonValueKind.True,
                    IsCustomizable: item.TryGetProperty("iscustomizable", out var cust) &&
                                    cust.TryGetProperty("Value", out var custVal) && custVal.ValueKind == JsonValueKind.True));
            }
        }

        return results;
    }

    public async Task<ViewDetail?> GetAsync(
        string orgUrl,
        Guid viewId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/savedqueries({viewId})" +
                  "?$select=savedqueryid,name,querytype,isdefault,iscustomizable,fetchxml,layoutxml,description";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
        var item = JsonDocument.Parse(raw).RootElement;

        return new ViewDetail(
            ViewId: item.TryGetGuid("savedqueryid"),
            Name: item.GetStringOrEmpty("name"),
            ViewType: MapQueryTypeToName(item.GetInt32OrZero("querytype")),
            IsDefault: item.TryGetProperty("isdefault", out var def) && def.ValueKind == JsonValueKind.True,
            IsCustomizable: item.TryGetProperty("iscustomizable", out var cust) &&
                            cust.TryGetProperty("Value", out var custVal) && custVal.ValueKind == JsonValueKind.True,
            FetchXml: item.GetStringOrNull("fetchxml"),
            LayoutXml: item.GetStringOrNull("layoutxml"),
            Description: item.GetStringOrNull("description"));
    }

    public async Task UpdateAsync(
        string orgUrl,
        Guid viewId,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/savedqueries({viewId})", properties, ct);
    }

    public async Task AddColumnAsync(
        string orgUrl,
        Guid viewId,
        string attributeLogicalName,
        int? width = null,
        CancellationToken ct = default)
    {
        var view = await GetAsync(orgUrl, viewId, ct)
                   ?? throw new InvalidOperationException($"View {viewId} not found.");

        var layoutXml = view.LayoutXml;
        if (string.IsNullOrWhiteSpace(layoutXml))
            throw new InvalidOperationException("View has no LayoutXml to modify.");

        var doc = XDocument.Parse(layoutXml);
        var row = doc.Descendants("row").FirstOrDefault()
                  ?? throw new InvalidOperationException("No <row> element found in LayoutXml.");

        // Avoid duplicate columns
        var existing = row.Elements("cell")
            .Any(c => c.Attribute("name")?.Value == attributeLogicalName);
        if (!existing)
        {
            var cell = new XElement("cell",
                new XAttribute("name", attributeLogicalName),
                new XAttribute("width", width ?? 100));
            row.Add(cell);
        }

        await UpdateAsync(orgUrl, viewId, new Dictionary<string, object?> { ["layoutxml"] = doc.ToString() }, ct);
    }

    public async Task SetSortAsync(
        string orgUrl,
        Guid viewId,
        string attributeLogicalName,
        bool descending,
        CancellationToken ct = default)
    {
        var view = await GetAsync(orgUrl, viewId, ct)
                   ?? throw new InvalidOperationException($"View {viewId} not found.");

        if (string.IsNullOrWhiteSpace(view.FetchXml))
            throw new InvalidOperationException("View has no FetchXml to modify.");

        var doc = XDocument.Parse(view.FetchXml);
        var entity = doc.Descendants("entity").FirstOrDefault()
                     ?? throw new InvalidOperationException("No <entity> element in FetchXml.");

        // Remove existing order elements
        entity.Elements("order").Remove();

        entity.Add(new XElement("order",
            new XAttribute("attribute", attributeLogicalName),
            new XAttribute("descending", descending.ToString().ToLowerInvariant())));

        await UpdateAsync(orgUrl, viewId, new Dictionary<string, object?> { ["fetchxml"] = doc.ToString() }, ct);
    }

    private static int MapViewType(string viewType) => viewType.ToLowerInvariant() switch
    {
        "public" => 0,
        "advanced find" => 1,
        "associated" => 2,
        "quickfind" => 4,
        _ => 0
    };

    private static string MapQueryTypeToName(int queryType) => queryType switch
    {
        0 => "Public",
        1 => "AdvancedFind",
        2 => "Associated",
        4 => "QuickFind",
        _ => $"Type{queryType}"
    };
}
