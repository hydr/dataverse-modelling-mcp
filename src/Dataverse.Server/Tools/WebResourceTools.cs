namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Models;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class WebResourceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "webresource_list")]
    [Description("List web resources, optionally filtered by a name prefix (e.g. 'sample_' or " +
                 "'sample_purchaseorder'). Returns webresourceid, name, displayname and the " +
                 "webresourcetype (1=HTML, 2=CSS, 3=JScript, 4=XML, 5=PNG, 6=JPG, 7=GIF, 8=XAP, " +
                 "9=XSL, 10=ICO, 11=SVG, 12=RESX).")]
    public static async Task<string> WebResourceList(
        WebResourceService svc,
        ConfigProvider config,
        [Description("Optional name prefix to filter on, e.g. a publisher prefix or a table prefix")] string? tableOrPrefix = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, tableOrPrefix, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "webresource_get")]
    [Description("Get a web resource by name or id. Text formats (HTML, CSS, JScript, XML, XSL, SVG, " +
                 "RESX) come back with their decoded content; binary formats report only the byte size. " +
                 "Note: the content returned is the PUBLISHED content — an unpublished change is not " +
                 "visible here until publish_customizations has run for the web resource.")]
    public static async Task<string> WebResourceGet(
        WebResourceService svc,
        ConfigProvider config,
        [Description("The web resource name (e.g. 'sample_purchaseorder_correct_price.js') or its GUID")] string nameOrId,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();

            Guid id;
            if (!Guid.TryParse(nameOrId, out id))
            {
                var found = await svc.FindIdByNameAsync(env.OrgUrl, nameOrId, ct);
                if (found is null)
                {
                    return JsonSerializer.Serialize(new { error = $"Web resource '{nameOrId}' not found." });
                }

                id = found.Value;
            }

            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "Web resource not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "webresource_usages")]
    [Description("Find where a web resource is referenced — ask before deleting one. Searches the " +
                 "platform's own dependency tracking plus the documents that name a web resource: " +
                 "form XML, ribbon diffs, the site map, and the lookups on modern commands. Pass " +
                 "`tables` to also search those tables' MERGED (compiled) ribbons: the stored ribbon " +
                 "diff only holds this environment's own changes, so a button coming from a managed " +
                 "solution is invisible without it. The result lists under notSearched what was not " +
                 "covered — read it before concluding the resource can go.")]
    public static async Task<string> WebResourceUsages(
        WebResourceUsageService svc,
        ConfigProvider config,
        [Description("Web resource name (e.g. 'xv_purchaseinvoice_js') or its GUID")] string name,
        [Description("Optional comma-separated table logical names whose merged ribbon should also be searched")] string? tables = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var tableList = string.IsNullOrWhiteSpace(tables)
                ? Array.Empty<string>()
                : tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await svc.FindUsagesAsync(env.OrgUrl, name, tableList, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "webresource_upsert")]
    [Description("Create or update a web resource. Pass either filePath (read from disk) or content " +
                 "(inline text); the payload is Base64-encoded for Dataverse. On create the " +
                 "webResourceType is inferred from the file extension when not given " +
                 "(.js=3, .html=1, .css=2, .xml=4, .png=5, .jpg=6, .gif=7, .xap=8, .xsl=9, .ico=10, " +
                 ".svg=11, .resx=12); on update the type cannot be changed. Optionally adds the web " +
                 "resource to a solution (component type 61) and publishes it. A web resource stays " +
                 "invisible to clients until published — leave publish=true unless you are batching.")]
    public static async Task<string> WebResourceUpsert(
        WebResourceService svc,
        SolutionService solutionSvc,
        PublishService publishSvc,
        ConfigProvider config,
        [Description("Unique name of the web resource, e.g. 'sample_purchaseorder_correct_price.js'")] string name,
        [Description("Path to a local file whose bytes become the content")] string? filePath = null,
        [Description("Inline text content (used when filePath is not given)")] string? content = null,
        [Description("Display name; defaults to the name on create")] string? displayName = null,
        [Description("Optional description")] string? description = null,
        [Description("webresourcetype option value; inferred from the extension when omitted")] int? webResourceType = null,
        [Description("Optional solution unique name — the web resource is added as component type 61")] string? solutionUniqueName = null,
        [Description("Publish the web resource after the write (default true)")] bool publish = true,
        CancellationToken ct = default)
    {
        try
        {
            if (filePath is null && content is null)
            {
                return JsonSerializer.Serialize(new { error = "Pass either filePath or content." });
            }

            byte[] bytes;
            if (filePath is not null)
            {
                if (!File.Exists(filePath))
                {
                    return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" });
                }

                bytes = await File.ReadAllBytesAsync(filePath, ct);
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(content!);
            }

            // The extension of the local file is a better type hint than a name without one.
            var typeHint = webResourceType
                           ?? WebResourceService.InferTypeFromExtension(name)
                           ?? (filePath is null ? null : WebResourceService.InferTypeFromExtension(filePath));

            var env = config.GetActiveEnvironment();
            var (id, created, type) = await svc.UpsertAsync(
                env.OrgUrl, name, bytes, displayName, description, typeHint, ct);

            var solutionComponentAdded = false;
            if (!string.IsNullOrWhiteSpace(solutionUniqueName))
            {
                await solutionSvc.AddComponentAsync(
                    env.OrgUrl, solutionUniqueName, id, WebResourceService.WebResourceComponentType, ct);
                solutionComponentAdded = true;
            }

            if (publish)
            {
                await publishSvc.PublishAsync(env.OrgUrl, webResources: new[] { id.ToString() }, ct: ct);
            }

            var result = new WebResourceUpsertResult(
                WebResourceId: id,
                Name: name,
                WebResourceType: type,
                WebResourceTypeName: WebResourceService.TypeName(type),
                Created: created,
                ContentBytes: bytes.Length,
                SolutionUniqueName: solutionUniqueName,
                SolutionComponentAdded: solutionComponentAdded,
                Published: publish);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
