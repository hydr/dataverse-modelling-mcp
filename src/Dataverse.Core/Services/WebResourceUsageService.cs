namespace Dataverse.Core.Services;

using System.Text.Json;
using Dataverse.Core.Clients;
using Dataverse.Core.Json;
using Dataverse.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Answers "can this web resource go?".
/// </summary>
/// <remarks>
/// There is no single place that records where a web resource is used, so the answer is assembled
/// from several: the platform's own dependency tracking, plus a text search of the documents that
/// reference web resources by name — form XML, ribbon diffs, the site map — plus the lookups on
/// modern commands.
/// <para>
/// The text searches exist because dependency tracking does not cover everything. A ribbon that
/// names a library in a <c>&lt;JavaScriptFunction&gt;</c> creates no dependency row, which is
/// exactly how a web resource ends up looking unused while a button still calls into it.
/// </para>
/// <para>
/// What this deliberately does <b>not</b> search is listed in <c>NotSearched</c> on the result. A
/// "no usages" answer that quietly skipped half the environment would be worse than no answer.
/// </para>
/// </remarks>
public sealed class WebResourceUsageService
{
    private readonly DataverseHttpClient _client;
    private readonly ComponentDependencyService _dependencies;
    private readonly RibbonService _ribbons;
    private readonly ILogger<WebResourceUsageService> _logger;

    public WebResourceUsageService(
        DataverseHttpClient client,
        ComponentDependencyService dependencies,
        RibbonService ribbons,
        ILogger<WebResourceUsageService> logger)
    {
        _client = client;
        _dependencies = dependencies;
        _ribbons = ribbons;
        _logger = logger;
    }

    /// <summary>
    /// Find where a web resource is referenced.
    /// </summary>
    /// <param name="tables">
    /// Tables whose <b>merged</b> ribbon should also be searched. The stored ribbon diff only holds
    /// this environment's changes, so a button that comes from a managed solution is invisible in it
    /// — only the compiled ribbon shows one, and it can only be fetched per table.
    /// </param>
    public async Task<WebResourceUsageReport> FindUsagesAsync(
        string orgUrl,
        string name,
        IEnumerable<string>? tables = null,
        CancellationToken ct = default)
    {
        var (id, resolvedName) = await ResolveAsync(orgUrl, name, ct);
        var literal = resolvedName.Replace("'", "''");

        var forms = await SearchAsync(
            orgUrl,
            $"api/data/v9.2/systemforms?$select=formid,name,objecttypecode,type"
            + $"&$filter=contains(formxml,'{literal}')",
            "Form", "formid", "name", "objecttypecode", ct);

        var ribbonDiffs = await SearchAsync(
            orgUrl,
            $"api/data/v9.2/ribbondiffs?$select=ribbondiffid,diffid,difftype"
            + $"&$filter=contains(rdx,'{literal}')",
            "RibbonDiff", "ribbondiffid", "diffid", null, ct);

        var siteMaps = await SearchAsync(
            orgUrl,
            $"api/data/v9.2/sitemaps?$select=sitemapid,sitemapname"
            + $"&$filter=contains(sitemapxml,'{literal}')",
            "SiteMap", "sitemapid", "sitemapname", null, ct);

        // Modern commands reference a web resource through real lookups, so this is exact rather
        // than a text match.
        var commands = await SearchAsync(
            orgUrl,
            "api/data/v9.2/appactions?$select=appactionid,name"
            + $"&$filter=_onclickeventjavascriptwebresourceid_value eq {id:D}"
            + $" or _iconwebresourceid_value eq {id:D}",
            "ModernCommand", "appactionid", "name", null, ct);

        var mergedRibbons = new List<WebResourceUsage>();
        var searchedTables = (tables ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        foreach (var table in searchedTables)
        {
            try
            {
                var xml = await _ribbons.RetrieveCompiledRibbonAsync(orgUrl, table, ct);
                if (xml.Contains(resolvedName, StringComparison.OrdinalIgnoreCase))
                    mergedRibbons.Add(new WebResourceUsage("MergedRibbon", Guid.Empty, table, table));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the merged ribbon of {Table}.", table);
            }
        }

        var platform = await _dependencies.GetDependenciesForDeleteAsync(orgUrl, id, 61, ct);

        var notSearched = new List<string>
        {
            "the content of other web resources (it is stored base64-encoded, so a server-side text "
            + "search is not possible)",
            "canvas apps, plug-in code and anything outside Dataverse"
        };

        if (searchedTables.Count == 0)
        {
            notSearched.Insert(0,
                "merged (compiled) ribbons — pass `tables` to search them. The stored ribbon diff "
                + "only holds this environment's own changes, so a button from a managed solution "
                + "does not show up above.");
        }

        var total = forms.Count + ribbonDiffs.Count + siteMaps.Count + commands.Count
                    + mergedRibbons.Count + platform.DependentCount;

        return new WebResourceUsageReport(
            Name: resolvedName,
            WebResourceId: id,
            IsUsed: total > 0,
            Summary: BuildSummary(resolvedName, forms, ribbonDiffs, siteMaps, commands, mergedRibbons, platform),
            Forms: forms,
            RibbonDiffs: ribbonDiffs,
            SiteMaps: siteMaps,
            ModernCommands: commands,
            MergedRibbons: mergedRibbons,
            PlatformDependencies: platform,
            NotSearched: notSearched);
    }

    public static string BuildSummary(
        string name,
        IReadOnlyList<WebResourceUsage> forms,
        IReadOnlyList<WebResourceUsage> ribbonDiffs,
        IReadOnlyList<WebResourceUsage> siteMaps,
        IReadOnlyList<WebResourceUsage> commands,
        IReadOnlyList<WebResourceUsage> mergedRibbons,
        ComponentDependencyReport platform)
    {
        var parts = new List<string>();
        if (forms.Count > 0) parts.Add($"{forms.Count} form(s)");
        if (ribbonDiffs.Count > 0) parts.Add($"{ribbonDiffs.Count} ribbon diff(s)");
        if (siteMaps.Count > 0) parts.Add($"{siteMaps.Count} site map(s)");
        if (commands.Count > 0) parts.Add($"{commands.Count} modern command(s)");
        if (mergedRibbons.Count > 0) parts.Add($"{mergedRibbons.Count} merged ribbon(s)");
        if (platform.DependentCount > 0) parts.Add($"{platform.DependentCount} platform dependency/dependencies");

        return parts.Count == 0
            ? $"No usage of '{name}' found in what was searched. See notSearched before concluding it "
              + "can be deleted."
            : $"'{name}' is referenced by " + string.Join(", ", parts) + ".";
    }

    private async Task<(Guid Id, string Name)> ResolveAsync(string orgUrl, string name, CancellationToken ct)
    {
        if (Guid.TryParse(name, out var byId))
        {
            var rawById = await _client.GetRawAsync(
                orgUrl, $"api/data/v9.2/webresourceset({byId:D})?$select=name", ct: ct);
            using var docById = JsonDocument.Parse(rawById);
            return (byId, docById.RootElement.GetStringOrEmpty("name"));
        }

        var raw = await _client.GetRawAsync(
            orgUrl,
            $"api/data/v9.2/webresourceset?$select=webresourceid,name&$filter=name eq '{name.Replace("'", "''")}'",
            ct: ct);
        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("value", out var items) || items.GetArrayLength() == 0)
            throw new InvalidOperationException($"Web resource '{name}' not found.");

        return (items[0].TryGetGuid("webresourceid"), items[0].GetStringOrEmpty("name"));
    }

    /// <summary>
    /// Run one usage query. A search that a tenant refuses must narrow the answer, not fail it —
    /// which is why the failure is logged and the section comes back empty.
    /// </summary>
    private async Task<List<WebResourceUsage>> SearchAsync(
        string orgUrl,
        string url,
        string kind,
        string idField,
        string nameField,
        string? contextField,
        CancellationToken ct)
    {
        var usages = new List<WebResourceUsage>();
        try
        {
            foreach (var row in await _client.GetAllPagesAsync(orgUrl, url, ct: ct))
            {
                usages.Add(new WebResourceUsage(
                    Kind: kind,
                    Id: row.TryGetGuid(idField),
                    Name: row.GetStringOrNull(nameField),
                    Context: contextField is null ? null : row.GetStringOrNull(contextField)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Usage search for {Kind} failed.", kind);
        }
        return usages;
    }
}
