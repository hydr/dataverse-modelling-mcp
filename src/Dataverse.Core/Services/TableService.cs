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

    /// <summary>
    /// Labels in languages the payload does not mention are kept instead of being dropped by the
    /// replace. Without this a round-trip through one locale silently deletes the others.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MergeLabels =
        new Dictionary<string, string> { ["MSCRM.MergeLabels"] = "true" };

    /// <summary>Update table metadata.</summary>
    /// <returns>
    /// The managed properties that were rewritten from a plain value into their object shape — see
    /// <see cref="ManagedPropertyNormalizer"/>.
    /// </returns>
    public async Task<IReadOnlyList<string>> UpdateAsync(
        string orgUrl,
        string logicalName,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        var normalized = ManagedPropertyNormalizer.Normalize(properties, ManagedPropertyScope.Entity);

        var current = await ReadDefinitionAsync(
            orgUrl, $"api/data/v9.2/EntityDefinitions(LogicalName='{logicalName}')", ct);
        var (body, metadataId) = MergeIntoDefinition(current, properties);

        await _client.PutAsync(
            orgUrl, $"api/data/v9.2/EntityDefinitions({metadataId:D})", body, MergeLabels, ct);

        return normalized;
    }

    /// <summary>Add a column to a table.</summary>
    /// <returns>The managed properties that were rewritten into their object shape.</returns>
    public async Task<IReadOnlyList<string>> AddColumnAsync(
        string orgUrl,
        string tableLogicalName,
        Dictionary<string, object?> attributeDefinition,
        CancellationToken ct = default)
    {
        var normalized = ManagedPropertyNormalizer.Normalize(
            attributeDefinition, ManagedPropertyScope.Attribute);

        await _client.PostAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')/Attributes",
            attributeDefinition,
            ct);

        return normalized;
    }

    /// <summary>Update a column's metadata.</summary>
    /// <returns>The managed properties that were rewritten into their object shape.</returns>
    public async Task<IReadOnlyList<string>> UpdateColumnAsync(
        string orgUrl,
        string tableLogicalName,
        string columnLogicalName,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        var normalized = ManagedPropertyNormalizer.Normalize(properties, ManagedPropertyScope.Attribute);

        var current = await ReadDefinitionAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')"
            + $"/Attributes(LogicalName='{columnLogicalName}')",
            ct);
        var (body, metadataId) = MergeIntoDefinition(current, properties);

        await _client.PutAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')/Attributes({metadataId:D})",
            body,
            MergeLabels,
            ct);

        return normalized;
    }

    /// <summary>
    /// Resolve a column's <c>MetadataId</c> — the id every dependency and solution call wants.
    /// </summary>
    public async Task<Guid> GetColumnMetadataIdAsync(
        string orgUrl,
        string tableLogicalName,
        string columnLogicalName,
        CancellationToken ct = default)
    {
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')"
            + $"/Attributes(LogicalName='{columnLogicalName}')?$select=MetadataId",
            ct: ct);
        using var doc = JsonDocument.Parse(raw);

        var id = doc.RootElement.TryGetGuid("MetadataId");
        if (id == Guid.Empty)
            throw new InvalidOperationException(
                $"Column '{columnLogicalName}' not found on table '{tableLogicalName}'.");

        return id;
    }

    /// <summary>Delete a column. Irreversible, and it takes the stored data with it.</summary>
    public async Task DeleteColumnAsync(
        string orgUrl,
        string tableLogicalName,
        string columnLogicalName,
        CancellationToken ct = default)
    {
        await _client.DeleteAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')"
            + $"/Attributes(LogicalName='{columnLogicalName}')",
            ct);
    }

    /// <summary>
    /// Read a metadata definition as a plain property bag, ready to be merged and written back.
    /// </summary>
    private async Task<Dictionary<string, JsonElement>> ReadDefinitionAsync(
        string orgUrl,
        string relativeUrl,
        CancellationToken ct)
    {
        var raw = await _client.GetRawAsync(orgUrl, relativeUrl, ct: ct);
        using var doc = JsonDocument.Parse(raw);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Unexpected metadata response for {relativeUrl}.");

        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // The context URL describes the response, not the definition — echoing it back is
            // meaningless and Dataverse has no use for it.
            if (property.NameEquals("@odata.context"))
                continue;
            bag[property.Name] = property.Value.Clone();
        }

        return bag;
    }

    /// <summary>
    /// Lay the caller's properties over the current definition and pull out the MetadataId.
    /// </summary>
    /// <remarks>
    /// A metadata update is a full replace: the endpoint takes the whole definition and keeps only
    /// what the body contains. Sending just the changed properties would therefore wipe everything
    /// else, so the current definition is read first and the caller's values are laid over it.
    /// The overlay is deliberately shallow — a caller who passes <c>DisplayName</c> means to replace
    /// that whole object, not to merge into it.
    /// <para>
    /// <c>@odata.type</c> survives from the current definition unless the caller sets it: for a
    /// column it names the concrete metadata type (e.g. <c>StringAttributeMetadata</c>) and the
    /// request is rejected without it.
    /// </para>
    /// </remarks>
    private static (Dictionary<string, object?> Body, Guid MetadataId) MergeIntoDefinition(
        Dictionary<string, JsonElement> current,
        Dictionary<string, object?> properties)
    {
        if (!current.TryGetValue("MetadataId", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var metadataId))
        {
            throw new InvalidOperationException(
                "The metadata definition carries no MetadataId — cannot address it for the update.");
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in current)
            body[key] = value;

        foreach (var (key, value) in properties)
            body[key] = value;

        return (body, metadataId);
    }
}
