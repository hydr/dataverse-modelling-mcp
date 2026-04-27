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
        var baseFilter = "category eq 0"; // Classic workflows only
        var combinedFilter = string.IsNullOrWhiteSpace(filter)
            ? baseFilter
            : $"{baseFilter} and ({filter})";

        var url = $"api/data/v9.2/workflows" +
                  $"?$filter={Uri.EscapeDataString(combinedFilter)}" +
                  $"&$select=workflowid,name,primaryentity,statecode,statuscode,_ownerid_value" +
                  $"&$top={top}";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
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
                  "_ownerid_value,description,xaml,createdon,modifiedon";

        var raw = await _client.GetRawAsync(orgUrl, url, ct);
        var item = JsonDocument.Parse(raw).RootElement;

        return new WorkflowDetail(
            WorkflowId: item.TryGetGuid("workflowid"),
            Name: item.GetStringOrEmpty("name"),
            PrimaryEntity: item.GetStringOrNull("primaryentity"),
            StateCode: item.GetInt32OrZero("statecode"),
            StatusCode: item.GetInt32OrZero("statuscode"),
            OwnerId: item.GetStringOrNull("_ownerid_value"),
            OwnerName: null,
            Description: item.GetStringOrNull("description"),
            Xaml: item.GetStringOrNull("xaml"),
            CreatedOn: item.GetDateTimeOrNull("createdon"),
            ModifiedOn: item.GetDateTimeOrNull("modifiedon"));
    }

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

