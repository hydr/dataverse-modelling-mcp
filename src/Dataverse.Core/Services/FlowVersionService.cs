namespace Dataverse.Core.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Read and restore version history for solution-aware cloud flows.
/// Backed by the Dataverse <c>componentversion</c> virtual entity, which holds one row per
/// Create/Update/Publish/Restore/Solution-Import event on the owning <c>workflow</c> row.
/// </summary>
public sealed class FlowVersionService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<FlowVersionService> _logger;

    public FlowVersionService(DataverseHttpClient client, ILogger<FlowVersionService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FlowVersionSummary>> ListAsync(
        string orgUrl,
        Guid flowId,
        int top = 50,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/workflows({flowId})/componentversions" +
                  $"?$top={top}&$orderby=createdon desc";

        var raw = await _client.GetRawAsync(orgUrl, url, includeFormattedValues: true, ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<FlowVersionSummary>();

        if (!doc.RootElement.TryGetProperty("value", out var items))
            return results;

        foreach (var item in items.EnumerateArray())
        {
            var op = item.GetInt32OrZero("operation");
            string opName = item.GetStringOrNull("operation@OData.Community.Display.V1.FormattedValue")
                ?? MapOperation(op);

            string? createdBy = item.GetStringOrNull("_createdby_value@OData.Community.Display.V1.FormattedValue");

            Guid? restoredFrom = null;
            if (item.TryGetProperty("_restoredfromversion_value", out var rfv) &&
                rfv.ValueKind == JsonValueKind.String &&
                Guid.TryParse(rfv.GetString(), out var rfvGuid))
            {
                restoredFrom = rfvGuid;
            }

            results.Add(new FlowVersionSummary(
                VersionId: item.TryGetGuid("componentversionid"),
                VersionName: item.GetStringOrEmpty("componentversionname"),
                Operation: op,
                OperationName: opName,
                CreatedOn: item.GetDateTimeOrNull("createdon"),
                CreatedByName: createdBy,
                RestoredFromVersionId: restoredFrom,
                ChangeSummary: item.GetStringOrNull("changesummary"),
                SystemChangeSummary: item.GetStringOrNull("systemchangesummary")));
        }

        return results;
    }

    public async Task<FlowVersionDetail?> GetAsync(
        string orgUrl,
        Guid flowId,
        Guid versionId,
        CancellationToken ct = default)
    {
        // The componentversion virtual entity rejects both direct RetrieveMultiple AND key-based
        // navigation lookups (`workflows({id})/componentversions({verId})` returns
        // "Get with NavigationKey is allowed only on Metadata Entities"). Resolve by filtering
        // the collection retrieved via the workflow nav property.
        var versions = await ListAsync(orgUrl, flowId, top: 200, ct);
        var match = versions.FirstOrDefault(v => v.VersionId == versionId);
        if (match is null)
            return null;

        return new FlowVersionDetail(
            VersionId: match.VersionId,
            VersionName: match.VersionName,
            Operation: match.Operation,
            OperationName: match.OperationName,
            CreatedOn: match.CreatedOn,
            CreatedByName: match.CreatedByName,
            ModifiedOn: null,
            ModifiedByName: null,
            RestoredFromVersionId: match.RestoredFromVersionId,
            WorkflowId: flowId,
            WorkflowName: null,
            ChangeSummary: match.ChangeSummary,
            SystemChangeSummary: match.SystemChangeSummary);
    }

    /// <summary>
    /// Restores a prior version by invoking the bound Dataverse action
    /// <c>Microsoft.Dynamics.CRM.RestoreComponentVersion</c> on the owning workflow record.
    /// This is the same call the Power Automate maker portal makes when a user clicks "Restore"
    /// in the version history panel. The flow is left in the Draft state with the snapshot's
    /// definition; the caller is responsible for publishing.
    /// </summary>
    public async Task<FlowRestoreResult> RestoreAsync(
        string orgUrl,
        Guid flowId,
        Guid versionId,
        string? changeSummary = null,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/workflows({flowId})/Microsoft.Dynamics.CRM.RestoreComponentVersion";
        // The bound action declares the parameter as "RestoringVersionId" (PascalCase). A regular
        // anonymous object would be camel-cased by the shared JsonSerializerOptions, so use a
        // Dictionary to keep the key verbatim.
        var body = new Dictionary<string, object?>
        {
            ["RestoringVersionId"] = $"componentversions({versionId})"
        };

        await _client.PostAsync(orgUrl, url, body, ct);

        return new FlowRestoreResult(
            NewVersionId: Guid.Empty,
            RestoredFromVersionId: versionId,
            WorkflowId: flowId,
            Message: changeSummary is null
                ? "Version restored as new draft on workflow. Publish to activate."
                : $"Version restored as new draft on workflow ({changeSummary}). Publish to activate.");
    }

    /// <summary>
    /// Publishes the current draft of a flow as a new "Published" component version. Mirrors the
    /// Power Automate maker portal's "Publish" button, which calls the unbound Dataverse action
    /// <c>PublishComponent</c>. By default the flow is NOT activated as part of publishing —
    /// activation is a separate runtime concern.
    /// </summary>
    public async Task PublishAsync(
        string orgUrl,
        Guid flowId,
        bool activateFlow = false,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/PublishComponent?ActivateFlowOnPublish={(activateFlow ? "true" : "false")}";
        var body = new Dictionary<string, object?>
        {
            ["Target"] = $"/workflows({flowId})"
        };
        await _client.PostAsync(orgUrl, url, body, ct);
    }

    /// <summary>
    /// Saves a draft of a flow without publishing. Mirrors the maker portal's "Save draft" button:
    /// PATCH on the workflow with header <c>mscrm.AsUnpublished: true</c>, which creates an Update
    /// component version row but does NOT promote the changes to the runtime.
    /// </summary>
    public async Task SaveDraftAsync(
        string orgUrl,
        Guid flowId,
        string clientData,
        string? name = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientdata"] = clientData
        };
        if (!string.IsNullOrWhiteSpace(name))
            body["name"] = name;

        var headers = new Dictionary<string, string>
        {
            ["mscrm.AsUnpublished"] = "true",
            ["If-Match"] = "*"
        };

        await _client.PatchAsync(orgUrl, $"api/data/v9.2/workflows({flowId})", body, headers, ct);
    }

    /// <summary>
    /// Creates a solution-aware cloud flow by writing a <c>workflow</c> entity row directly into
    /// Dataverse (category=5, type=1). Unlike the Power Automate Flow API used by
    /// <c>CloudFlowService.CreateAsync</c>, this produces a proper Dataverse row that supports
    /// version history, <c>flow_get_clientdata</c>, <c>flow_save_draft</c>, and all other
    /// Dataverse-backed tools.
    /// </summary>
    public async Task<Guid> CreateSolutionAwareAsync(
        string orgUrl,
        string name,
        string clientData,
        string solutionUniqueName,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["category"] = 5,
            ["type"] = 1,
            ["primaryentity"] = "none",
            ["statecode"] = 0,
            ["statuscode"] = 1,
            ["clientdata"] = clientData
        };

        var headers = new Dictionary<string, string>
        {
            ["MSCRM.SolutionUniqueName"] = solutionUniqueName
        };

        var raw = await _client.PostRawAsync(
            orgUrl,
            "api/data/v9.2/workflows",
            body,
            extraHeaders: headers,
            requestRepresentation: true,
            ct);

        var doc = JsonDocument.Parse(raw);
        return Guid.Parse(doc.RootElement.GetProperty("workflowid").GetString()!);
    }

    /// <summary>
    /// Returns the raw <c>clientdata</c> string of a workflow record — the stringified Logic Apps
    /// JSON wrapper (<c>{ properties: { connectionReferences, definition }, schemaVersion }</c>).
    /// This is what <c>flow_save_draft</c> expects on the way back. <c>flow_get</c> only returns the
    /// inner <c>definition</c>, losing the <c>connectionReferences</c>.
    /// </summary>
    public async Task<string> GetClientDataAsync(string orgUrl, Guid flowId, CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/workflows({flowId})?$select=clientdata",
            includeFormattedValues: false,
            ct);
        var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("clientdata").GetString() ?? string.Empty;
    }

    /// <summary>
    /// Surgical update of a single parameter on one action's <c>inputs.parameters</c> object.
    /// Reads <c>clientdata</c>, mutates only the targeted key, then saves it as draft. Avoids
    /// round-tripping the full 30 KB+ clientdata through callers for routine FetchXML / param edits.
    /// </summary>
    /// <param name="parameterName">
    /// The key directly under <c>inputs.parameters</c>. May contain slashes (e.g.
    /// <c>item/subject</c>, <c>item/activitypointer_activity_parties</c>) — they are treated as part of the
    /// key, not as a path separator.
    /// </param>
    /// <param name="valueJson">
    /// New value as JSON. May be a string ("foo"), number (42), bool (true), object ({...}), array ([...]).
    /// </param>
    /// <param name="publish">If true, also publish the draft immediately.</param>
    public async Task PatchActionInputAsync(
        string orgUrl,
        Guid flowId,
        string actionName,
        string parameterName,
        string valueJson,
        bool publish = false,
        bool activateFlow = false,
        CancellationToken ct = default)
    {
        var clientData = await GetClientDataAsync(orgUrl, flowId, ct);
        var rootNode = JsonNode.Parse(clientData)
            ?? throw new InvalidOperationException("clientdata is empty or not parseable.");

        var actions = rootNode["properties"]?["definition"]?["actions"]
            ?? throw new InvalidOperationException("clientdata.properties.definition.actions not found.");

        var action = actions[actionName]
            ?? throw new InvalidOperationException($"Action '{actionName}' not found in flow.");

        var parameters = action["inputs"]?["parameters"]
            ?? throw new InvalidOperationException($"Action '{actionName}' has no inputs.parameters.");

        parameters[parameterName] = JsonNode.Parse(valueJson);

        var newClientData = rootNode.ToJsonString();
        await SaveDraftAsync(orgUrl, flowId, newClientData, null, ct);

        if (publish)
            await PublishAsync(orgUrl, flowId, activateFlow, ct);
    }

    /// <summary>
    /// Atomic wrapper: <c>SaveDraft</c> + <c>Publish</c> in one call. Mirrors the Maker UI's
    /// "Save and Publish" button.
    /// </summary>
    public async Task SaveDraftAndPublishAsync(
        string orgUrl,
        Guid flowId,
        string clientData,
        string? name = null,
        bool activateFlow = false,
        CancellationToken ct = default)
    {
        await SaveDraftAsync(orgUrl, flowId, clientData, name, ct);
        await PublishAsync(orgUrl, flowId, activateFlow, ct);
    }

    /// <summary>
    /// Read-only sanity for a FetchXML: runs it against the given entity set and reports validity,
    /// result count, and an optional sample. Useful pre-flight check before patching a flow.
    /// </summary>
    public async Task<FetchXmlValidationResult> ValidateFetchXmlAsync(
        string orgUrl,
        string entitySet,
        string fetchXml,
        int sampleSize = 0,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"api/data/v9.2/{entitySet}?fetchXml={Uri.EscapeDataString(fetchXml)}";
            var raw = await _client.GetRawAsync(orgUrl, url, includeFormattedValues: false, ct);
            var doc = JsonDocument.Parse(raw);
            var count = 0;
            var samples = new List<string>();
            if (doc.RootElement.TryGetProperty("value", out var items))
            {
                count = items.GetArrayLength();
                if (sampleSize > 0)
                {
                    foreach (var item in items.EnumerateArray().Take(sampleSize))
                        samples.Add(item.GetRawText());
                }
            }
            return new FetchXmlValidationResult(true, count, samples, null);
        }
        catch (HttpRequestException ex)
        {
            return new FetchXmlValidationResult(false, 0, [], ex.Message);
        }
        catch (Exception ex)
        {
            return new FetchXmlValidationResult(false, 0, [], ex.Message);
        }
    }

    private static string MapOperation(int value) => value switch
    {
        0 => "Create",
        1 => "Update",
        2 => "Publish",
        3 => "Restore",
        4 => "Solution Import",
        _ => value.ToString()
    };
}
