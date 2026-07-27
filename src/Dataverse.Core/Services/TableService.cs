namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

public sealed class TableService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<TableService> _logger;

    public TableService(DataverseHttpClient client, ILogger<TableService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TableSummary>> ListAsync(
        string orgUrl,
        string? filter = null,
        string? solutionUniqueName = null,
        CancellationToken ct = default)
    {
        // MetadataId is selected so the solution filter can match against solutioncomponents.objectid.
        var url = "api/data/v9.2/EntityDefinitions?$select=MetadataId,LogicalName,DisplayName,EntitySetName,TableType,IsCustomEntity";

        // The metadata endpoint does support $filter on simple metadata properties
        // (e.g. "IsCustomEntity eq true"). Pass it through instead of silently dropping it — the
        // parameter used to be accepted and ignored, which made table_list return the full table list.
        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += "&$filter=" + Uri.EscapeDataString(filter);
        }

        // The metadata endpoint has no notion of solutions, so the restriction is resolved separately
        // via solutioncomponents (componenttype 1 = Entity) and applied client-side on MetadataId.
        HashSet<Guid>? solutionEntityIds = null;
        if (!string.IsNullOrWhiteSpace(solutionUniqueName))
            solutionEntityIds = await GetSolutionEntityIdsAsync(orgUrl, solutionUniqueName, ct);

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var doc = JsonDocument.Parse(raw);
        var results = new List<TableSummary>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (solutionEntityIds is not null && !solutionEntityIds.Contains(item.TryGetGuid("MetadataId")))
                    continue;

                var displayName = item.TryGetProperty("DisplayName", out var dn)
                    ? dn.GetStringOrNull("UserLocalizedLabel.Label") ?? dn.GetStringOrNull("LocalizedLabels[0].Label")
                    : null;

                // Simpler: just get the raw string if it's a LocalizedLabel object
                string? displayNameStr = null;
                if (item.TryGetProperty("DisplayName", out var dnEl))
                {
                    if (dnEl.TryGetProperty("UserLocalizedLabel", out var ull))
                        displayNameStr = ull.GetStringOrNull("Label");
                }

                results.Add(new TableSummary(
                    LogicalName: item.GetStringOrEmpty("LogicalName"),
                    DisplayName: displayNameStr,
                    EntitySetName: item.GetStringOrNull("EntitySetName"),
                    TableType: item.GetStringOrNull("TableType"),
                    IsCustomEntity: item.TryGetProperty("IsCustomEntity", out var ice) && ice.ValueKind == JsonValueKind.True));
            }
        }

        return results;
    }

    /// <summary>
    /// Resolve the <c>MetadataId</c>s of all entities contained in a solution, via
    /// <c>solutioncomponents</c> filtered on componenttype 1 (Entity). The components' objectid
    /// is the entity's MetadataId.
    /// </summary>
    private async Task<HashSet<Guid>> GetSolutionEntityIdsAsync(
        string orgUrl,
        string solutionUniqueName,
        CancellationToken ct)
    {
        var solUrl = $"api/data/v9.2/solutions?$filter=uniquename eq '{solutionUniqueName}'&$select=solutionid";
        var solRaw = await _client.GetRawAsync(orgUrl, solUrl, ct: ct);
        using var solDoc = JsonDocument.Parse(solRaw);

        var solutionId = Guid.Empty;
        if (solDoc.RootElement.TryGetProperty("value", out var solutions) && solutions.GetArrayLength() > 0)
            solutionId = solutions[0].TryGetGuid("solutionid");

        if (solutionId == Guid.Empty)
            throw new InvalidOperationException($"Solution '{solutionUniqueName}' not found.");

        var compUrl = $"api/data/v9.2/solutioncomponents" +
                      $"?$filter=_solutionid_value eq {solutionId} and componenttype eq 1" +
                      "&$select=objectid";

        var compRaw = await _client.GetRawAsync(orgUrl, compUrl, ct: ct);
        using var compDoc = JsonDocument.Parse(compRaw);

        var ids = new HashSet<Guid>();
        if (compDoc.RootElement.TryGetProperty("value", out var components))
        {
            foreach (var comp in components.EnumerateArray())
            {
                var objectId = comp.TryGetGuid("objectid");
                if (objectId != Guid.Empty)
                    ids.Add(objectId);
            }
        }

        _logger.LogInformation(
            "Solution '{Solution}' contains {Count} entity components.", solutionUniqueName, ids.Count);
        return ids;
    }

    public async Task<TableDetail?> GetAsync(
        string orgUrl,
        string logicalName,
        CancellationToken ct = default)
    {
        var url = $"api/data/v9.2/EntityDefinitions(LogicalName='{logicalName}')" +
                  "?$expand=Attributes($select=LogicalName,DisplayName,AttributeType,RequiredLevel,IsCustomAttribute)";

        var raw = await _client.GetRawAsync(orgUrl, url, ct: ct);
        var item = JsonDocument.Parse(raw).RootElement;

        string? displayName = null;
        string? pluralDisplayName = null;
        if (item.TryGetProperty("DisplayName", out var dn) &&
            dn.TryGetProperty("UserLocalizedLabel", out var ull))
            displayName = ull.GetStringOrNull("Label");

        if (item.TryGetProperty("DisplayCollectionName", out var dcn) &&
            dcn.TryGetProperty("UserLocalizedLabel", out var ullPlural))
            pluralDisplayName = ullPlural.GetStringOrNull("Label");

        string? description = null;
        if (item.TryGetProperty("Description", out var desc) &&
            desc.TryGetProperty("UserLocalizedLabel", out var descUll))
            description = descUll.GetStringOrNull("Label");

        var columns = new List<ColumnSummary>();
        if (item.TryGetProperty("Attributes", out var attrs))
        {
            foreach (var attr in attrs.EnumerateArray())
            {
                string? colDisplayName = null;
                if (attr.TryGetProperty("DisplayName", out var colDn) &&
                    colDn.TryGetProperty("UserLocalizedLabel", out var colUll))
                    colDisplayName = colUll.GetStringOrNull("Label");

                string? requiredLevel = null;
                if (attr.TryGetProperty("RequiredLevel", out var rl))
                    requiredLevel = rl.GetStringOrNull("Value");

                columns.Add(new ColumnSummary(
                    LogicalName: attr.GetStringOrEmpty("LogicalName"),
                    DisplayName: colDisplayName,
                    AttributeType: attr.GetStringOrNull("AttributeType") ?? "Unknown",
                    IsRequired: requiredLevel is "ApplicationRequired" or "SystemRequired",
                    IsCustomAttribute: attr.TryGetProperty("IsCustomAttribute", out var ica) && ica.ValueKind == JsonValueKind.True));
            }
        }

        return new TableDetail(
            LogicalName: item.GetStringOrEmpty("LogicalName"),
            DisplayName: displayName,
            PluralDisplayName: pluralDisplayName,
            EntitySetName: item.GetStringOrNull("EntitySetName"),
            Description: description,
            TableType: item.GetStringOrNull("TableType"),
            IsCustomEntity: item.TryGetProperty("IsCustomEntity", out var isCustom) && isCustom.ValueKind == JsonValueKind.True,
            IsAuditEnabled: item.TryGetProperty("IsAuditEnabled", out var isAudit) &&
                            isAudit.TryGetProperty("Value", out var auditVal) && auditVal.ValueKind == JsonValueKind.True,
            Attributes: columns);
    }

    public async Task CreateAsync(
        string orgUrl,
        string logicalName,
        string displayName,
        string pluralDisplayName,
        string? description = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            SchemaName = logicalName,
            DisplayName = new
            {
                LocalizedLabels = new[] { new { Label = displayName, LanguageCode = 1033 } }
            },
            DisplayCollectionName = new
            {
                LocalizedLabels = new[] { new { Label = pluralDisplayName, LanguageCode = 1033 } }
            },
            Description = description is not null ? new
            {
                LocalizedLabels = new[] { new { Label = description, LanguageCode = 1033 } }
            } : null,
            OwnershipType = "UserOwned",
            HasActivities = false,
            HasNotes = false
        };

        await _client.PostAsync(orgUrl, "api/data/v9.2/EntityDefinitions", body, ct);
    }

    public async Task UpdateAsync(
        string orgUrl,
        string logicalName,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(orgUrl, $"api/data/v9.2/EntityDefinitions(LogicalName='{logicalName}')", properties, ct);
    }

    public async Task AddColumnAsync(
        string orgUrl,
        string tableLogicalName,
        Dictionary<string, object?> attributeDefinition,
        CancellationToken ct = default)
    {
        await _client.PostAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')/Attributes",
            attributeDefinition,
            ct);
    }

    public async Task UpdateColumnAsync(
        string orgUrl,
        string tableLogicalName,
        string columnLogicalName,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await _client.PatchAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')/Attributes(LogicalName='{columnLogicalName}')",
            properties,
            ct);
    }
}
