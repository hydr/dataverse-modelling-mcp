namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Turns the GUIDs in a solution's component list into names.
/// </summary>
/// <remarks>
/// A component list is <c>objectid</c> + <c>componenttype</c> and nothing else. For a solution with
/// a hundred components that is unusable without resolving every GUID by hand against
/// <c>EntityDefinitions</c>, <c>webresourceset</c> and friends — which is exactly the work this
/// class does once, in bulk, per component type.
/// <para>
/// Resolution is best-effort throughout: a type without a known name source, a failed lookup, or a
/// row that no longer exists leaves the name null. It must never be the reason a solution read
/// fails, so every lookup is wrapped and only logged.
/// </para>
/// </remarks>
public sealed class SolutionComponentNameResolver
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger _logger;

    public SolutionComponentNameResolver(DataverseHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Component types whose name lives in an ordinary table, keyed by <c>componenttype</c>.
    /// Metadata-backed types (1 = Entity, 2 = Attribute) are not here — they come from
    /// <c>EntityDefinitions</c> and are handled separately.
    /// </summary>
    /// <remarks>
    /// Collection and field names for types 26, 29, 60, 61, 66, 90, 91 and 92 were verified against a
    /// live environment. The rest are best-effort: a wrong collection or field just makes the lookup
    /// fail, which leaves the name null exactly as an unmapped type would — never an error.
    /// </remarks>
    private static readonly IReadOnlyDictionary<int, ComponentNameSource> Sources =
        new Dictionary<int, ComponentNameSource>
        {
            [20] = new("roles", "roleid", "name"),
            [26] = new("savedqueries", "savedqueryid", "name"),
            [29] = new("workflows", "workflowid", "name"),
            [59] = new("savedqueryvisualizations", "savedqueryvisualizationid", "name"),
            [60] = new("systemforms", "formid", "name"),
            [61] = new("webresourceset", "webresourceid", "name"),
            [66] = new("customcontrols", "customcontrolid", "name"),
            [70] = new("fieldsecurityprofiles", "fieldsecurityprofileid", "name"),
            [90] = new("plugintypes", "plugintypeid", "name"),
            [91] = new("pluginassemblies", "pluginassemblyid", "name"),
            [92] = new("sdkmessageprocessingsteps", "sdkmessageprocessingstepid", "name"),
            [95] = new("serviceendpoints", "serviceendpointid", "name"),
            [380] = new("environmentvariabledefinitions", "environmentvariabledefinitionid", "schemaname"),
        };

    private sealed record ComponentNameSource(string Collection, string IdField, string NameField);

    /// <summary>
    /// Dataverse rejects an over-long URL, so id filters are sent in chunks of OR-ed equality
    /// comparisons rather than as one filter per solution.
    /// </summary>
    private const int FilterChunkSize = 20;

    /// <summary>
    /// Attribute names are only resolvable per owning table, so the cost grows with the number of
    /// tables in the solution. Beyond this many, attribute names are skipped rather than turning one
    /// solution read into dozens of metadata requests.
    /// </summary>
    private const int MaxTablesForAttributeLookup = 50;

    /// <summary>
    /// Resolve names for the given components. Returns a fresh list in the same order, with
    /// <see cref="SolutionComponent.Name"/> filled in wherever it could be resolved.
    /// </summary>
    /// <param name="attributeParents">
    /// Optional map from a column's MetadataId to its owning table's MetadataId. There is no global
    /// attribute collection to query, so without a parent a column can only be found by searching
    /// the tables that happen to be in the same list. Callers that already know the parent — a
    /// dependency response carries it — should pass it and get an exact answer for one request per
    /// table instead of a scan.
    /// </param>
    public async Task<IReadOnlyList<SolutionComponent>> ResolveAsync(
        string orgUrl,
        IReadOnlyList<SolutionComponent> components,
        IReadOnlyDictionary<Guid, Guid>? attributeParents = null,
        CancellationToken ct = default)
    {
        if (components.Count == 0)
            return components;

        var names = new Dictionary<(int Type, Guid Id), string>();

        // Tables and columns come from the metadata endpoint, everything else from a normal table.
        var tableIds = components.Where(c => c.ComponentType == 1).Select(c => c.ComponentId).ToHashSet();
        var attributeIds = components.Where(c => c.ComponentType == 2).Select(c => c.ComponentId).ToHashSet();

        Dictionary<Guid, string> tableNames = new();
        if (tableIds.Count > 0 || attributeIds.Count > 0)
            tableNames = await GetTableNamesAsync(orgUrl, ct);

        foreach (var id in tableIds)
            if (tableNames.TryGetValue(id, out var logicalName))
                names[(1, id)] = logicalName;

        if (attributeIds.Count > 0)
        {
            // Tables to search for the columns: the ones a caller pointed at explicitly, then the
            // ones that are in the list anyway.
            var owningTables = new List<string>();

            if (attributeParents is not null)
            {
                owningTables.AddRange(attributeParents
                    .Where(kv => attributeIds.Contains(kv.Key))
                    .Select(kv => tableNames.TryGetValue(kv.Value, out var n) ? n : null)
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }

            owningTables.AddRange(tableIds
                .Select(id => tableNames.TryGetValue(id, out var n) ? n : null)
                .Where(n => n is not null)
                .Select(n => n!)
                .Where(n => !owningTables.Contains(n, StringComparer.OrdinalIgnoreCase)));

            foreach (var table in owningTables.Take(MaxTablesForAttributeLookup))
            {
                if (attributeIds.Count == 0)
                    break;

                foreach (var (id, name) in await TryResolveAttributeNamesAsync(orgUrl, table, ct))
                {
                    if (!attributeIds.Remove(id))
                        continue;
                    names[(2, id)] = $"{table}.{name}";
                }
            }
        }

        foreach (var group in components.Where(c => Sources.ContainsKey(c.ComponentType)).GroupBy(c => c.ComponentType))
        {
            var source = Sources[group.Key];
            var ids = group.Select(c => c.ComponentId).Distinct().ToList();
            foreach (var (id, name) in await TryResolveFromTableAsync(orgUrl, source, ids, ct))
                names[(group.Key, id)] = name;
        }

        return components
            .Select(c => names.TryGetValue((c.ComponentType, c.ComponentId), out var name) ? c with { Name = name } : c)
            .ToList();
    }

    /// <summary>
    /// Map every table's <c>MetadataId</c> to its logical name. One request for the whole
    /// environment — cheaper than one request per id, and the payload stays small because only two
    /// properties are selected.
    /// </summary>
    public async Task<Dictionary<Guid, string>> GetTableNamesAsync(string orgUrl, CancellationToken ct = default)
    {
        var map = new Dictionary<Guid, string>();
        try
        {
            var rows = await _client.GetAllPagesAsync(
                orgUrl,
                "api/data/v9.2/EntityDefinitions?$select=MetadataId,LogicalName",
                ct: ct);

            foreach (var row in rows)
            {
                var id = row.TryGetGuid("MetadataId");
                var name = row.GetStringOrNull("LogicalName");
                if (id != Guid.Empty && !string.IsNullOrEmpty(name))
                    map[id] = name!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve table names for solution components.");
        }
        return map;
    }

    private async Task<Dictionary<Guid, string>> TryResolveAttributeNamesAsync(
        string orgUrl,
        string tableLogicalName,
        CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>();
        try
        {
            var rows = await _client.GetAllPagesAsync(
                orgUrl,
                $"api/data/v9.2/EntityDefinitions(LogicalName='{tableLogicalName}')/Attributes"
                + "?$select=MetadataId,LogicalName",
                ct: ct);

            foreach (var row in rows)
            {
                var id = row.TryGetGuid("MetadataId");
                var name = row.GetStringOrNull("LogicalName");
                if (id != Guid.Empty && !string.IsNullOrEmpty(name))
                    map[id] = name!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve column names of table {Table}.", tableLogicalName);
        }
        return map;
    }

    private async Task<Dictionary<Guid, string>> TryResolveFromTableAsync(
        string orgUrl,
        ComponentNameSource source,
        IReadOnlyList<Guid> ids,
        CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>();

        for (var offset = 0; offset < ids.Count; offset += FilterChunkSize)
        {
            var chunk = ids.Skip(offset).Take(FilterChunkSize).ToList();
            var filter = string.Join(" or ", chunk.Select(id => $"{source.IdField} eq {id:D}"));
            try
            {
                var rows = await _client.GetAllPagesAsync(
                    orgUrl,
                    $"api/data/v9.2/{source.Collection}"
                    + $"?$select={source.IdField},{source.NameField}&$filter={filter}",
                    ct: ct);

                foreach (var row in rows)
                {
                    var id = row.TryGetGuid(source.IdField);
                    var name = row.GetStringOrNull(source.NameField);
                    if (id != Guid.Empty && !string.IsNullOrEmpty(name))
                        map[id] = name!;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Could not resolve component names from {Collection}.", source.Collection);
            }
        }

        return map;
    }
}
