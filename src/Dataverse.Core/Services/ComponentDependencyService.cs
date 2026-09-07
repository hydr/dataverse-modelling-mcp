namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Answers "what breaks if I delete this?" before anything is deleted.
/// </summary>
/// <remarks>
/// Wraps the <c>RetrieveDependenciesForDelete</c> function. Calling it by hand is easy to get wrong:
/// it is a function with parameters, so the OData syntax needs parameter aliases
/// (<c>?ObjectId=@p1&amp;ComponentType=@p2</c> plus <c>@p1=…&amp;@p2=…</c>), and getting that wrong
/// returns an HTML error page rather than a JSON error. On top of that the raw response is nothing
/// but GUIDs and type codes, and an empty collection reads just like a call that did not work.
/// </remarks>
public sealed class ComponentDependencyService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<ComponentDependencyService> _logger;

    public ComponentDependencyService(DataverseHttpClient client, ILogger<ComponentDependencyService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// List everything that depends on a component, with names resolved.
    /// </summary>
    /// <param name="objectId">
    /// The component's own id — for a table or column its <c>MetadataId</c>, the same id
    /// <c>solution_get</c> reports as <c>componentId</c>.
    /// </param>
    public async Task<ComponentDependencyReport> GetDependenciesForDeleteAsync(
        string orgUrl,
        Guid objectId,
        int componentType,
        CancellationToken ct = default)
    {
        // Parameter aliases are required for a function with parameters; inlining the values
        // produces a Runtime Error page instead of a usable response.
        var url = "api/data/v9.2/RetrieveDependenciesForDelete(ObjectId=@p1,ComponentType=@p2)"
                  + $"?@p1={objectId:D}&@p2={componentType}";

        var rows = await _client.GetAllPagesAsync(orgUrl, url, ct: ct);

        var dependents = new List<ComponentDependency>(rows.Count);
        var attributeParents = new Dictionary<Guid, Guid>();
        var toName = new List<SolutionComponent>();

        foreach (var row in rows)
        {
            // Every row repeats the examined component's own parent, which is the only way to name
            // it when it is a column — there is no global attribute collection to look it up in.
            var ownParent = row.TryGetGuid("requiredcomponentparentid");
            if (componentType == 2 && ownParent != Guid.Empty)
                attributeParents[objectId] = ownParent;

            var id = row.TryGetGuid("dependentcomponentobjectid");
            if (id == Guid.Empty)
                continue;

            var type = row.GetInt32OrZero("dependentcomponenttype");
            var parent = row.TryGetGuid("dependentcomponentparentid");

            dependents.Add(new ComponentDependency(
                ComponentId: id,
                ComponentType: type,
                ComponentTypeName: SolutionService.MapComponentTypeName(type),
                Name: null,
                ParentId: parent == Guid.Empty ? null : parent,
                ParentName: null,
                DependencyType: row.GetInt32OrZero("dependencytype")));

            toName.Add(new SolutionComponent(id, type, SolutionService.MapComponentTypeName(type)));
            if (type == 2 && parent != Guid.Empty)
                attributeParents[id] = parent;
        }

        var resolver = new SolutionComponentNameResolver(_client, _logger);
        var named = await resolver.ResolveAsync(orgUrl, toName, attributeParents, ct);
        var nameByKey = named
            .Where(c => c.Name is not null)
            .GroupBy(c => (c.ComponentType, c.ComponentId))
            .ToDictionary(g => g.Key, g => g.First().Name!);

        // Parents are tables, so one bulk map covers every parent name at once.
        var tableNames = dependents.Any(d => d.ParentId is not null)
            ? await resolver.GetTableNamesAsync(orgUrl, ct)
            : [];

        for (var i = 0; i < dependents.Count; i++)
        {
            var d = dependents[i];
            dependents[i] = d with
            {
                Name = nameByKey.TryGetValue((d.ComponentType, d.ComponentId), out var n) ? n : null,
                ParentName = d.ParentId is { } pid && tableNames.TryGetValue(pid, out var pn) ? pn : null
            };
        }

        var typeName = SolutionService.MapComponentTypeName(componentType);
        var selfName = await TryResolveSelfNameAsync(
            orgUrl, resolver, objectId, componentType, attributeParents, ct);

        var summary = dependents.Count == 0
            ? $"No dependencies — nothing in this environment requires {typeName} "
              + $"{selfName ?? objectId.ToString("D")}, so it can be deleted."
            : $"{dependents.Count} component(s) depend on {typeName} "
              + $"{selfName ?? objectId.ToString("D")} and would break if it were deleted: "
              + string.Join(", ", dependents
                  .GroupBy(d => d.ComponentTypeName)
                  .Select(g => $"{g.Count()}× {g.Key}"));

        return new ComponentDependencyReport(
            ComponentId: objectId,
            ComponentType: componentType,
            ComponentTypeName: typeName,
            ComponentName: selfName,
            CanDelete: dependents.Count == 0,
            DependentCount: dependents.Count,
            Summary: summary,
            Dependents: dependents);
    }

    /// <summary>
    /// Name the component being examined, so the summary reads as a sentence about a thing rather
    /// than about a GUID. Best-effort — a name is nice to have, not required.
    /// </summary>
    private async Task<string?> TryResolveSelfNameAsync(
        string orgUrl,
        SolutionComponentNameResolver resolver,
        Guid objectId,
        int componentType,
        IReadOnlyDictionary<Guid, Guid> attributeParents,
        CancellationToken ct)
    {
        try
        {
            var resolved = await resolver.ResolveAsync(
                orgUrl,
                [new SolutionComponent(objectId, componentType, SolutionService.MapComponentTypeName(componentType))],
                attributeParents,
                ct);
            return resolved.Count > 0 ? resolved[0].Name : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the name of component {ComponentId}.", objectId);
            return null;
        }
    }
}
