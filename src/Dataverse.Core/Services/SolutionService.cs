namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class SolutionService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<SolutionService> _logger;

    public SolutionService(DataverseHttpClient client, ILogger<SolutionService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SolutionSummary>> ListAsync(
        string orgUrl,
        CancellationToken ct = default)
    {
        var url = "api/data/v9.2/solutions?$filter=isvisible eq true" +
                  "&$select=solutionid,uniquename,friendlyname,version,ismanaged,_publisherid_value" +
                  "&$expand=publisherid($select=friendlyname,uniquename)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<SolutionSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                string? pubName = null;
                if (item.TryGetProperty("publisherid", out var pubEl) && pubEl.ValueKind == JsonValueKind.Object)
                    pubName = pubEl.GetStringOrNull("friendlyname");

                results.Add(new SolutionSummary(
                    SolutionId: item.TryGetGuid("solutionid"),
                    UniqueName: item.GetStringOrEmpty("uniquename"),
                    FriendlyName: item.GetStringOrEmpty("friendlyname"),
                    Version: item.GetStringOrEmpty("version"),
                    IsManaged: item.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
                    PublisherName: pubName));
            }
        }

        return results;
    }

    public async Task<SolutionDetail?> GetAsync(
        string orgUrl,
        string uniqueName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/solutions?$filter=uniquename eq '{uniqueName}'" +
                  "&$select=solutionid,uniquename,friendlyname,version,ismanaged,description,installedon" +
                  "&$expand=publisherid($select=friendlyname,uniquename),solution_solutioncomponent($select=objectid,componenttype,rootcomponentbehavior)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            return null;

        var item = items[0];
        var components = new List<SolutionComponent>();

        if (item.TryGetProperty("solution_solutioncomponent", out var comps))
        {
            foreach (var comp in comps.EnumerateArray())
            {
                components.Add(new SolutionComponent(
                    ComponentId: comp.TryGetGuid("objectid"),
                    ComponentType: comp.GetInt32OrZero("componenttype"),
                    ComponentTypeName: MapComponentType(comp.GetInt32OrZero("componenttype")),
                    RootComponentId: comp.TryGetProperty("rootcomponentbehavior", out _)
                        ? comp.TryGetGuid("rootsolutioncomponentid")
                        : null));
            }
        }

        string? detailPubName = null;
        if (item.TryGetProperty("publisherid", out var detailPubEl) && detailPubEl.ValueKind == JsonValueKind.Object)
            detailPubName = detailPubEl.GetStringOrNull("friendlyname");

        return new SolutionDetail(
            SolutionId: item.TryGetGuid("solutionid"),
            UniqueName: item.GetStringOrEmpty("uniquename"),
            FriendlyName: item.GetStringOrEmpty("friendlyname"),
            Version: item.GetStringOrEmpty("version"),
            IsManaged: item.TryGetProperty("ismanaged", out var managed) && managed.ValueKind == JsonValueKind.True,
            PublisherName: detailPubName,
            Description: item.GetStringOrNull("description"),
            InstalledOn: item.GetDateTimeOrNull("installedon"),
            Components: components);
    }

    public async Task CreateAsync(
        string orgUrl,
        string uniqueName,
        string displayName,
        string publisherUniqueName,
        string version,
        CancellationToken ct = default)
    {
        // Resolve publisher ID first
        var pubUrl = $"api/data/v9.2/publishers?$filter=uniquename eq '{publisherUniqueName}'&$select=publisherid";
        var pubRaw = await _client.GetRawAsync(orgUrl, pubUrl, ct: ct);
        var pubDoc = JsonDocument.Parse(pubRaw);
        Guid publisherId = Guid.Empty;
        if (pubDoc.RootElement.TryGetProperty("value", out var pubs) && pubs.GetArrayLength() > 0)
            publisherId = pubs[0].TryGetGuid("publisherid");

        if (publisherId == Guid.Empty)
            throw new InvalidOperationException($"Publisher '{publisherUniqueName}' not found.");

        var body = new
        {
            uniquename = uniqueName,
            friendlyname = displayName,
            version,
            publisherid = $"/publishers({publisherId})"
        };

        await _client.PostAsync(orgUrl, "api/data/v9.2/solutions", body, ct);
    }

    public async Task<SolutionExportResult> ExportAsync(
        string orgUrl,
        string uniqueName,
        bool managed,
        CancellationToken ct = default)
    {
        var parameters = new { SolutionName = uniqueName, Managed = managed };
        var result = await _client.ExecuteActionAsync<JsonElement?>(orgUrl, "ExportSolution", parameters, ct);

        string? base64 = null;
        if (result is JsonElement el && el.TryGetProperty("ExportSolutionFile", out var fileProp))
            base64 = fileProp.GetString();

        return new SolutionExportResult(uniqueName, managed, FilePath: null, Base64Content: base64);
    }

    public async Task ImportAsync(
        string orgUrl,
        string zipBase64,
        bool overwriteUnmanaged,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            CustomizationFile = zipBase64,
            OverwriteUnmanagedCustomizations = overwriteUnmanaged,
            PublishWorkflows = true,
            ImportJobId = Guid.NewGuid()
        };

        await _client.ExecuteActionAsync(orgUrl, "ImportSolution", parameters, ct);
    }

    public async Task AddComponentAsync(
        string orgUrl,
        string solutionUniqueName,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType,
            SolutionUniqueName = solutionUniqueName,
            AddRequiredComponents = false
        };

        await _client.ExecuteActionAsync(orgUrl, "AddSolutionComponent", parameters, ct);
    }

    public async Task RemoveComponentAsync(
        string orgUrl,
        string solutionUniqueName,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType,
            SolutionUniqueName = solutionUniqueName
        };

        await _client.ExecuteActionAsync(orgUrl, "RemoveSolutionComponent", parameters, ct);
    }

    public async Task<SolutionLayerInfo> CheckLayersAsync(
        string orgUrl,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/msdyn_solutionhistories" +
                  $"?$filter=msdyn_solutionid ne null" +
                  $"&$select=msdyn_name,msdyn_solutionversion";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var layers = new List<string>();

        if (doc.RootElement.TryGetProperty("value", out var items))
            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetStringOrNull("msdyn_name");
                if (name is not null) layers.Add(name);
            }

        return new SolutionLayerInfo(componentId, componentType, layers);
    }

    public async Task RemoveActiveLayerAsync(
        string orgUrl,
        Guid componentId,
        int componentType,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            ComponentId = componentId,
            ComponentType = componentType
        };

        await _client.ExecuteActionAsync(orgUrl, "RemoveActiveCustomizations", parameters, ct);
    }

    public async Task DeployPipelineAsync(
        string orgUrl,
        string pipelineId,
        string stageId,
        CancellationToken ct = default)
    {
        var body = new
        {
            StageId = stageId
        };

        await _client.PostAsync(orgUrl, $"api/data/v9.2/pipelines({pipelineId})/Deploy", body, ct);
    }

    private static string MapComponentType(int type) => type switch
    {
        1 => "Entity",
        2 => "Attribute",
        3 => "Relationship",
        9 => "OptionSet",
        14 => "ConnectionRole",
        24 => "Workflow",
        26 => "SavedQuery",
        29 => "Report",
        44 => "WebResource",
        60 => "SiteMap",
        61 => "PluginType",
        62 => "PluginAssembly",
        92 => "Role",
        _ => $"Type{type}"
    };
}
