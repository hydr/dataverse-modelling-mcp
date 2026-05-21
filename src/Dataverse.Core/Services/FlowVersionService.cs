namespace Dataverse.Core.Services;

using System.Text.Json;
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
