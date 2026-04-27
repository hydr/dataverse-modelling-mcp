namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class WorkflowService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<WorkflowService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public WorkflowService(DataverseHttpClient client, ILogger<WorkflowService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(
        string orgUrl,
        string? filter = null,
        int top = 50,
        CancellationToken ct = default)
    {
        var baseFilter = "category eq 0 and type eq 1"; // Classic workflows, definitions only (type 2 = internal activation copies)
        var combinedFilter = string.IsNullOrWhiteSpace(filter)
            ? baseFilter
            : $"{baseFilter} and ({filter})";

        var url = $"api/data/v9.2/workflows" +
                  $"?$filter={Uri.EscapeDataString(combinedFilter)}" +
                  $"&$select=workflowid,name,primaryentity,statecode,statuscode,_ownerid_value" +
                  $"&$top={top}";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<WorkflowSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new WorkflowSummary(
                    WorkflowId: item.TryGetGuid("workflowid"),
                    Name: item.GetStringOrEmpty("name"),
                    PrimaryEntity: item.GetStringOrNull("primaryentity"),
                    StateCode: item.GetInt32OrZero("statecode"),
                    StatusCode: item.GetInt32OrZero("statuscode"),
                    OwnerId: item.GetStringOrNull("_ownerid_value"),
                    OwnerName: null));
            }
        }

        return results;
    }

    public async Task<WorkflowDetail?> GetAsync(
        string orgUrl,
        Guid workflowId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/workflows({workflowId})" +
                  "?$select=workflowid,name,primaryentity,statecode,statuscode," +
                  "_ownerid_value,description,xaml,createdon,modifiedon," +
                  "ondemand,triggerattribute," +
                  "createstage,updatestage,deletestage," +
                  "scope,mode,runas,istransacted,rank,logcontent,asyncautodelete";

        var raw = await _client.GetRawAsync(orgUrl, url, includeFormattedValues: true, ct);
        var item = JsonDocument.Parse(raw).RootElement;

        string? ownerName = null;
        if (item.TryGetProperty("_ownerid_value@OData.Community.Display.V1.FormattedValue", out var ownerFv))
            ownerName = ownerFv.GetString();

        return new WorkflowDetail(
            WorkflowId: item.TryGetGuid("workflowid"),
            Name: item.GetStringOrEmpty("name"),
            PrimaryEntity: item.GetStringOrNull("primaryentity"),
            StateCode: item.GetInt32OrZero("statecode"),
            StatusCode: item.GetInt32OrZero("statuscode"),
            OwnerId: item.GetStringOrNull("_ownerid_value"),
            OwnerName: ownerName,
            Description: item.GetStringOrNull("description"),
            Xaml: item.GetStringOrNull("xaml"),
            CreatedOn: item.GetDateTimeOrNull("createdon"),
            ModifiedOn: item.GetDateTimeOrNull("modifiedon"),
            OnDemand: item.TryGetProperty("ondemand", out var od) && od.ValueKind == JsonValueKind.True,
            IsOnCreate: item.GetInt32OrZero("createstage") != 0,
            IsOnUpdate: item.GetInt32OrZero("updatestage") != 0,
            IsOnDelete: item.GetInt32OrZero("deletestage") != 0,
            TriggerAttribute: item.GetStringOrNull("triggerattribute"),
            CreateStage: MapStage(item.GetInt32OrZero("createstage")),
            UpdateStage: MapStage(item.GetInt32OrZero("updatestage")),
            DeleteStage: MapStage(item.GetInt32OrZero("deletestage")),
            Scope: MapScope(item.GetInt32OrZero("scope")),
            Mode: item.GetInt32OrZero("mode") == 1 ? "Realtime" : "Background",
            RunAs: item.GetInt32OrZero("runas") == 1 ? "CallingUser" : "Owner",
            IsTransacted: item.TryGetProperty("istransacted", out var tr) && tr.ValueKind == JsonValueKind.True,
            Rank: item.GetInt32OrZero("rank"),
            LogContent: MapLogContent(item.GetInt32OrZero("logcontent")),
            AsyncAutoDelete: item.TryGetProperty("asyncautodelete", out var aad) && aad.ValueKind == JsonValueKind.True);
    }

    private static string? MapStage(int value) => value switch
    {
        20 => "PreOperation",
        40 => "PostOperation",
        _ => null
    };

    private static string MapScope(int value) => value switch
    {
        1 => "User",
        2 => "BusinessUnit",
        3 => "ParentChildBusinessUnit",
        4 => "Organization",
        _ => value.ToString()
    };

    private static string MapLogContent(int value) => value switch
    {
        0 => "None",
        1 => "Details",
        2 => "All",
        _ => value.ToString()
    };

    public async Task<Guid> CreateAsync(
        string orgUrl,
        string name,
        string primaryEntity,
        string? description = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["category"] = 0,
            ["primaryentity"] = primaryEntity,
            ["description"] = description,
            ["statecode"] = 0,
            ["statuscode"] = 1
        };

        var raw = await _client.PostAsync<JsonElement?>(orgUrl, "api/data/v9.2/workflows", body, ct);
        // The created ID comes from OData-EntityId response header; fall back to re-querying by name
        // For simplicity we do a follow-up list to get the ID
        var created = await ListAsync(orgUrl, $"name eq '{name}'", 1, ct);
        return created.Count > 0 ? created[0].WorkflowId : Guid.Empty;
    }

    public async Task UpdateAsync(
        string orgUrl,
        Guid workflowId,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", properties, ct);
    }

    public async Task SetStateAsync(
        string orgUrl,
        Guid workflowId,
        bool activate,
        CancellationToken ct = default)
    {
        // statecode 1 = Activated (statuscode 2), statecode 0 = Draft (statuscode 1)
        var body = new
        {
            statecode = activate ? 1 : 0,
            statuscode = activate ? 2 : 1
        };
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", body, ct);
    }

    public async Task AssignAsync(
        string orgUrl,
        Guid workflowId,
        string ownerType,
        Guid ownerId,
        CancellationToken ct = default)
    {
        var entityName = ownerType.Equals("team", StringComparison.OrdinalIgnoreCase) ? "teams" : "systemusers";
        var body = new
        {
            ownerid = $"/api/data/v9.2/{entityName}({ownerId})"
        };
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({workflowId})", body, ct);
    }

    public async Task<IReadOnlyList<WorkflowActivitySummary>> ListActivitiesAsync(
        string orgUrl,
        string? nameFilter = null,
        CancellationToken ct = default)
    {
        var filter = "workflowactivitygroupname ne null";
        if (!string.IsNullOrWhiteSpace(nameFilter))
            filter += $" and contains(name, '{nameFilter}')";

        var url = "api/data/v9.2/plugintypes" +
                  $"?$filter={Uri.EscapeDataString(filter)}" +
                  "&$select=plugintypeid,name,assemblyname,version,description,workflowactivitygroupname,typename" +
                  "&$expand=pluginassemblyid($select=name,version)" +
                  "&$orderby=assemblyname,name";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<WorkflowActivitySummary>();

        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;

        foreach (var item in items.EnumerateArray())
        {
            var typeName = item.GetStringOrNull("typename") ?? item.GetStringOrEmpty("name");
            var asmName = item.GetStringOrEmpty("assemblyname");
            var version = item.GetStringOrEmpty("version");

            // Build AssemblyQualifiedName from pluginassemblyid expand if available
            string assemblyQualifiedName;
            if (item.TryGetProperty("pluginassemblyid", out var asmEl) && asmEl.ValueKind == JsonValueKind.Object)
            {
                var fullAsmName = asmEl.GetStringOrNull("name") ?? asmName;
                var asmVersion = asmEl.GetStringOrNull("version") ?? version;
                assemblyQualifiedName = $"{typeName}, {fullAsmName}, Version={asmVersion}, Culture=neutral, PublicKeyToken=null";
            }
            else
            {
                assemblyQualifiedName = $"{typeName}, {asmName}";
            }

            results.Add(new WorkflowActivitySummary(
                PluginTypeId: item.TryGetGuid("plugintypeid"),
                Name: item.GetStringOrEmpty("name"),
                AssemblyQualifiedName: assemblyQualifiedName,
                AssemblyName: asmName,
                Version: version,
                Description: item.GetStringOrNull("description"),
                WorkflowActivityGroupName: item.GetInt32OrZero("workflowactivitygroupname")));
        }

        return results;
    }

    public async Task<WorkflowActivityDetail?> GetActivityParametersAsync(
        string orgUrl,
        Guid pluginTypeId,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/plugintypes({pluginTypeId})" +
                  "?$select=plugintypeid,name,assemblyname,version,description,typename" +
                  "&$expand=pluginassemblyid($select=name,version)," +
                  "plugintype_plugintypestatistic($select=plugintypestatisticid)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;

        var typeName = item.GetStringOrNull("typename") ?? item.GetStringOrEmpty("name");
        var asmName = item.GetStringOrEmpty("assemblyname");
        var version = item.GetStringOrEmpty("version");

        string assemblyQualifiedName;
        if (item.TryGetProperty("pluginassemblyid", out var asmEl) && asmEl.ValueKind == JsonValueKind.Object)
        {
            var fullAsmName = asmEl.GetStringOrNull("name") ?? asmName;
            var asmVersion = asmEl.GetStringOrNull("version") ?? version;
            assemblyQualifiedName = $"{typeName}, {fullAsmName}, Version={asmVersion}, Culture=neutral, PublicKeyToken=null";
        }
        else
        {
            assemblyQualifiedName = $"{typeName}, {asmName}";
        }

        // Fetch parameters separately via plugintypeattributes
        var paramUrl = $"api/data/v9.2/plugintypeattributes" +
                       $"?$filter=_plugintypeid_value eq {pluginTypeId}" +
                       "&$select=name,parametertype,direction,isrequired,description" +
                       "&$orderby=direction,name";

        var paramRaw = await _client.GetRawAsync(orgUrl, paramUrl, ct: ct);
        var paramDoc = JsonDocument.Parse(paramRaw);
        var parameters = new List<WorkflowActivityParameter>();

        if (paramDoc.RootElement.TryGetProperty("value", out var paramItems))
        {
            foreach (var p in paramItems.EnumerateArray())
            {
                var direction = p.GetInt32OrZero("direction") == 1 ? "Output" : "Input";
                parameters.Add(new WorkflowActivityParameter(
                    Name: p.GetStringOrEmpty("name"),
                    ParameterType: p.GetStringOrNull("parametertype") ?? "String",
                    Direction: direction,
                    IsRequired: p.TryGetProperty("isrequired", out var req) && req.ValueKind == JsonValueKind.True,
                    Description: p.GetStringOrNull("description")));
            }
        }

        return new WorkflowActivityDetail(
            PluginTypeId: item.TryGetGuid("plugintypeid"),
            Name: item.GetStringOrEmpty("name"),
            AssemblyQualifiedName: assemblyQualifiedName,
            AssemblyName: asmName,
            Version: version,
            Description: item.GetStringOrNull("description"),
            Parameters: parameters);
    }

    public async Task<WorkflowValidationReport> ValidateAsync(
        string orgUrl,
        Guid workflowId,
        CancellationToken ct = default)
    {
        var detail = await GetAsync(orgUrl, workflowId, ct);
        if (detail is null)
            return new WorkflowValidationReport(workflowId, "Unknown", false, ["Workflow not found."]);

        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(detail.Xaml))
            issues.Add("Workflow has no XAML definition.");

        if (string.IsNullOrWhiteSpace(detail.PrimaryEntity))
            issues.Add("Workflow has no primary entity set.");

        if (detail.StateCode != 1)
            issues.Add("Workflow is not activated (statecode != 1).");

        return new WorkflowValidationReport(workflowId, detail.Name, issues.Count == 0, issues);
    }
}

