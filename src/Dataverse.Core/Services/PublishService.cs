namespace Dataverse.Core.Services;

using System.Security;
using System.Text;
using Dataverse.Core.Clients;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps the <c>PublishXml</c> / <c>PublishAllXml</c> actions. Almost every customization made
/// through the Web API (forms, views, web resources, ribbons, modern commands, site map) stays
/// invisible to clients until it is published.
/// </summary>
/// <remarks>
/// For a form the publish is not a nicety — it is the only way the change becomes readable at all.
/// A <c>PATCH</c> of <c>systemform.formxml</c> writes the unpublished form, while a <c>GET</c>
/// returns the published one, so a read straight after the write keeps returning the old XML and the
/// old <c>versionnumber</c> indefinitely. Measured on a live org: still the old value after 120
/// seconds, then current immediately after <c>PublishXml</c>, with <c>versionnumber</c> jumping from
/// 55888235 to 64095408.
/// <para>
/// Table and column metadata behaves differently and is <b>not</b> publish-gated for reading back:
/// <c>EntityDefinitions</c> is eventually consistent and catches up on its own (measured: ~3s after
/// an update, ~16s after a delete). Clients still need a publish to pick the change up.
/// </para>
/// <para>
/// The element set below is the complete documented one
/// (learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.publishxmlrequest.parameterxml).
/// There is deliberately no element for code components: <c>PublishXml</c> cannot publish a custom
/// control, so a PCF change has to travel by import or <c>pac pcf push</c>.
/// </para>
/// </remarks>
public sealed class PublishService
{
    private readonly DataverseHttpClient _client;
    private readonly WebResourceService _webResources;
    private readonly ILogger<PublishService> _logger;

    public PublishService(
        DataverseHttpClient client,
        WebResourceService webResources,
        ILogger<PublishService> logger)
    {
        _client = client;
        _webResources = webResources;
        _logger = logger;
    }

    /// <summary>
    /// Publish the given components, or everything when <paramref name="all"/> is true.
    /// Web resources may be given as GUIDs or as names — names are resolved to ids first, because
    /// <c>PublishXml</c> only accepts ids inside <c>&lt;webresources&gt;</c>.
    /// </summary>
    /// <param name="dashboards">Dashboard (SystemForm) ids. Only ids work here, not names.</param>
    /// <param name="optionSets">Global choice unique names.</param>
    /// <param name="siteMap">Publish the site map. It is a singleton, so no value is needed.</param>
    /// <param name="ribbons">Publish the application ribbon. Also a singleton.</param>
    /// <returns>The ParameterXml that was sent, or null for <c>PublishAllXml</c>.</returns>
    public async Task<string?> PublishAsync(
        string orgUrl,
        IEnumerable<string>? entities = null,
        IEnumerable<string>? webResources = null,
        bool all = false,
        IEnumerable<string>? dashboards = null,
        IEnumerable<string>? optionSets = null,
        bool siteMap = false,
        bool ribbons = false,
        CancellationToken ct = default)
    {
        if (all)
        {
            await _client.ExecuteActionAsync(orgUrl, "PublishAllXml", new { }, ct);
            _logger.LogInformation("PublishAllXml completed for {OrgUrl}.", orgUrl);
            return null;
        }

        var entityList = (entities ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var webResourceIds = new List<Guid>();
        foreach (var wr in (webResources ?? Array.Empty<string>()).Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            var value = wr.Trim();
            if (Guid.TryParse(value, out var id))
            {
                webResourceIds.Add(id);
                continue;
            }

            var resolved = await _webResources.FindIdByNameAsync(orgUrl, value, ct)
                           ?? throw new InvalidOperationException($"Web resource '{value}' not found.");
            webResourceIds.Add(resolved);
        }

        var dashboardIds = new List<Guid>();
        foreach (var dashboard in (dashboards ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            if (!Guid.TryParse(dashboard.Trim(), out var dashboardId))
                throw new ArgumentException($"Dashboard '{dashboard}' is not a GUID — PublishXml only takes ids here.");
            dashboardIds.Add(dashboardId);
        }

        var optionSetList = (optionSets ?? Array.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entityList.Count == 0 && webResourceIds.Count == 0 && dashboardIds.Count == 0
            && optionSetList.Count == 0 && !siteMap && !ribbons)
        {
            throw new ArgumentException(
                "Nothing to publish — pass entities, webResources, dashboards, optionSets, "
                + "siteMap, ribbons, or all=true.");
        }

        var parameterXml = BuildParameterXml(
            entityList, webResourceIds, dashboardIds, optionSetList, siteMap, ribbons);
        await _client.ExecuteActionAsync(orgUrl, "PublishXml", new { ParameterXml = parameterXml }, ct);
        _logger.LogInformation(
            "PublishXml completed: {EntityCount} entities, {WebResourceCount} web resources, "
            + "{DashboardCount} dashboards, {OptionSetCount} choices, siteMap={SiteMap}, ribbons={Ribbons}.",
            entityList.Count, webResourceIds.Count, dashboardIds.Count, optionSetList.Count, siteMap, ribbons);

        return parameterXml;
    }

    /// <summary>Assemble the <c>&lt;importexportxml&gt;</c> document PublishXml expects.</summary>
    /// <remarks>
    /// Entities carry a logical name, web resources and dashboards an id, choices a unique name, and
    /// the site map and the application ribbon are singletons whose element is left empty. These six
    /// are the whole documented vocabulary — anything else in the document is ignored rather than
    /// rejected, which is why an invented element (a <c>&lt;customcontrols&gt;</c>, say) would look
    /// like it worked.
    /// </remarks>
    public static string BuildParameterXml(
        IReadOnlyList<string> entities,
        IReadOnlyList<Guid> webResourceIds,
        IReadOnlyList<Guid>? dashboardIds = null,
        IReadOnlyList<string>? optionSets = null,
        bool siteMap = false,
        bool ribbons = false)
    {
        var sb = new StringBuilder("<importexportxml>");

        if (entities.Count > 0)
        {
            sb.Append("<entities>");
            foreach (var e in entities)
            {
                sb.Append("<entity>").Append(SecurityElement.Escape(e)).Append("</entity>");
            }
            sb.Append("</entities>");
        }

        if (dashboardIds is { Count: > 0 })
        {
            sb.Append("<dashboards>");
            foreach (var id in dashboardIds)
            {
                sb.Append("<dashboard>").Append(id.ToString("D")).Append("</dashboard>");
            }
            sb.Append("</dashboards>");
        }

        if (optionSets is { Count: > 0 })
        {
            sb.Append("<optionsets>");
            foreach (var name in optionSets)
            {
                sb.Append("<optionset>").Append(SecurityElement.Escape(name)).Append("</optionset>");
            }
            sb.Append("</optionsets>");
        }

        if (webResourceIds.Count > 0)
        {
            sb.Append("<webresources>");
            foreach (var id in webResourceIds)
            {
                sb.Append("<webresource>").Append(id.ToString("D")).Append("</webresource>");
            }
            sb.Append("</webresources>");
        }

        if (siteMap)
            sb.Append("<sitemaps><sitemap></sitemap></sitemaps>");

        if (ribbons)
            sb.Append("<ribbons><ribbon></ribbon></ribbons>");

        sb.Append("</importexportxml>");
        return sb.ToString();
    }
}
