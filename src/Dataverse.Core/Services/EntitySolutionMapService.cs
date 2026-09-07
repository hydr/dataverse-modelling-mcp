namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Answers "which solution carries a change to this table?" in one call.
/// </summary>
/// <remarks>
/// The answer hangs entirely on <c>rootcomponentbehavior</c>, and the same table routinely sits in
/// several solutions with different values — so "add the column to the solution" is a no-op in one
/// and necessary in the next. Working that out by hand means one query per solution plus a mental
/// model of the behaviour codes, which is exactly the kind of thing to get wrong twice in a session.
/// <para>
/// Subcomponents are found through their <c>rootsolutioncomponentid</c>, which points back at the
/// table's own membership row. That beats matching on type and owning entity: it is what the
/// platform itself records, so it needs no guessing about which form or column belongs to which
/// table.
/// </para>
/// </remarks>
public sealed class EntitySolutionMapService
{
    private readonly DataverseHttpClient _client;
    private readonly ILogger<EntitySolutionMapService> _logger;

    public EntitySolutionMapService(DataverseHttpClient client, ILogger<EntitySolutionMapService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<EntitySolutionMap> GetAsync(
        string orgUrl,
        string logicalName,
        bool resolveComponentNames = true,
        CancellationToken ct = default)
    {
        var metadataId = await ResolveTableMetadataIdAsync(orgUrl, logicalName, ct);

        var rootRows = await _client.GetAllPagesAsync(
            orgUrl,
            $"api/data/v9.2/solutioncomponents?$filter=objectid eq {metadataId:D} and componenttype eq 1"
            + "&$select=solutioncomponentid,rootcomponentbehavior,_solutionid_value",
            ct: ct);

        var solutions = new List<EntitySolutionMembership>(rootRows.Count);
        var resolver = new SolutionComponentNameResolver(_client, _logger);

        foreach (var row in rootRows)
        {
            var rowId = row.TryGetGuid("solutioncomponentid");
            var solutionId = row.TryGetGuid("_solutionid_value");
            if (rowId == Guid.Empty || solutionId == Guid.Empty)
                continue;

            int? behavior = row.TryGetProperty("rootcomponentbehavior", out var rb)
                            && rb.ValueKind == JsonValueKind.Number
                ? rb.GetInt32()
                : null;

            var (uniqueName, isManaged) = await ReadSolutionAsync(orgUrl, solutionId, ct);
            var subcomponents = await ReadSubcomponentsAsync(orgUrl, rowId, ct);

            if (resolveComponentNames && subcomponents.Count > 0)
                subcomponents = [.. await resolver.ResolveAsync(orgUrl, subcomponents, ct: ct)];

            solutions.Add(new EntitySolutionMembership(
                SolutionUniqueName: uniqueName,
                SolutionId: solutionId,
                IsManaged: isManaged,
                RootComponentBehavior: behavior,
                RootComponentBehaviorName: SolutionService.MapRootComponentBehavior(behavior),
                SolutionComponentId: rowId,
                SubcomponentCount: subcomponents.Count,
                Subcomponents: subcomponents));
        }

        solutions.Sort((a, b) => string.Compare(a.SolutionUniqueName, b.SolutionUniqueName, StringComparison.OrdinalIgnoreCase));

        return new EntitySolutionMap(
            LogicalName: logicalName,
            MetadataId: metadataId,
            SolutionCount: solutions.Count,
            Summary: BuildSummary(logicalName, solutions),
            Solutions: solutions);
    }

    /// <summary>
    /// State the consequence, not just the codes — the codes are what nobody remembers.
    /// </summary>
    public static string BuildSummary(string logicalName, IReadOnlyList<EntitySolutionMembership> solutions)
    {
        if (solutions.Count == 0)
            return $"'{logicalName}' is not a component of any solution in this environment.";

        var carriers = solutions.Where(s => s.RootComponentBehavior == 0).Select(s => s.SolutionUniqueName).ToList();
        var explicitOnes = solutions.Where(s => s.RootComponentBehavior is 1 or 2).ToList();

        var parts = new List<string>();

        parts.Add(carriers.Count > 0
            ? $"Forms and columns of '{logicalName}' travel automatically with "
              + string.Join(", ", carriers) + " (rootcomponentbehavior 0)."
            : $"No solution holds '{logicalName}' with rootcomponentbehavior 0, so no solution picks "
              + "up its forms and columns automatically.");

        if (explicitOnes.Count > 0)
        {
            parts.Add("Subcomponents must be added explicitly in "
                      + string.Join(", ", explicitOnes.Select(s =>
                          $"{s.SolutionUniqueName} (behaviour {s.RootComponentBehavior}, "
                          + $"{s.SubcomponentCount} held explicitly)")) + ".");
        }

        return string.Join(" ", parts);
    }

    private async Task<Guid> ResolveTableMetadataIdAsync(string orgUrl, string logicalName, CancellationToken ct)
    {
        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/EntityDefinitions(LogicalName='{logicalName}')?$select=MetadataId",
            ct: ct);
        using var doc = JsonDocument.Parse(raw);

        var id = doc.RootElement.TryGetGuid("MetadataId");
        if (id == Guid.Empty)
            throw new InvalidOperationException($"Table '{logicalName}' not found.");

        return id;
    }

    private async Task<(string UniqueName, bool IsManaged)> ReadSolutionAsync(
        string orgUrl,
        Guid solutionId,
        CancellationToken ct)
    {
        try
        {
            var raw = await _client.GetRawAsync(
                orgUrl,
                $"api/data/v9.2/solutions({solutionId:D})?$select=uniquename,ismanaged",
                ct: ct);
            using var doc = JsonDocument.Parse(raw);
            return (
                doc.RootElement.GetStringOrEmpty("uniquename"),
                doc.RootElement.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read solution {SolutionId}.", solutionId);
            return (solutionId.ToString("D"), false);
        }
    }

    private async Task<List<SolutionComponent>> ReadSubcomponentsAsync(
        string orgUrl,
        Guid rootSolutionComponentId,
        CancellationToken ct)
    {
        var rows = await _client.GetAllPagesAsync(
            orgUrl,
            "api/data/v9.2/solutioncomponents"
            + $"?$filter=rootsolutioncomponentid eq {rootSolutionComponentId:D}"
            + "&$select=solutioncomponentid,objectid,componenttype,rootcomponentbehavior",
            ct: ct);

        var components = new List<SolutionComponent>(rows.Count);
        foreach (var row in rows)
        {
            var rowId = row.TryGetGuid("solutioncomponentid");

            // The root row references itself, which is not a subcomponent of anything.
            if (rowId == rootSolutionComponentId)
                continue;

            var type = row.GetInt32OrZero("componenttype");
            components.Add(new SolutionComponent(
                ComponentId: row.TryGetGuid("objectid"),
                ComponentType: type,
                ComponentTypeName: SolutionService.MapComponentTypeName(type),
                SolutionComponentId: rowId == Guid.Empty ? null : rowId));
        }

        return components;
    }
}
