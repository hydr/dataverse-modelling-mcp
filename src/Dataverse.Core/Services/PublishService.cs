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
    /// Publish the given entities and web resources, or everything when <paramref name="all"/> is true.
    /// Web resources may be given as GUIDs or as names — names are resolved to ids first, because
    /// <c>PublishXml</c> only accepts ids inside <c>&lt;webresources&gt;</c>.
    /// </summary>
    /// <returns>The ParameterXml that was sent, or null for <c>PublishAllXml</c>.</returns>
    public async Task<string?> PublishAsync(
        string orgUrl,
        IEnumerable<string>? entities = null,
        IEnumerable<string>? webResources = null,
        bool all = false,
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

        if (entityList.Count == 0 && webResourceIds.Count == 0)
        {
            throw new ArgumentException("Nothing to publish — pass entities, webResources, or all=true.");
        }

        var parameterXml = BuildParameterXml(entityList, webResourceIds);
        await _client.ExecuteActionAsync(orgUrl, "PublishXml", new { ParameterXml = parameterXml }, ct);
        _logger.LogInformation(
            "PublishXml completed: {EntityCount} entities, {WebResourceCount} web resources.",
            entityList.Count, webResourceIds.Count);

        return parameterXml;
    }

    /// <summary>Assemble the <c>&lt;importexportxml&gt;</c> document PublishXml expects.</summary>
    public static string BuildParameterXml(IReadOnlyList<string> entities, IReadOnlyList<Guid> webResourceIds)
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

        if (webResourceIds.Count > 0)
        {
            sb.Append("<webresources>");
            foreach (var id in webResourceIds)
            {
                sb.Append("<webresource>").Append(id.ToString("D")).Append("</webresource>");
            }
            sb.Append("</webresources>");
        }

        sb.Append("</importexportxml>");
        return sb.ToString();
    }
}
