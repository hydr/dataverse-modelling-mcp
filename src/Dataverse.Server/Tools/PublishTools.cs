namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class PublishTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "publish_customizations")]
    [Description("Publish customizations so clients pick them up. Runs PublishXml with a " +
                 "ParameterXml assembled from the given components, or PublishAllXml when all=true. " +
                 "Needed after nearly every Web API change to forms, views, ribbons, modern " +
                 "commands, web resources or the site map — before publishing, the change exists in " +
                 "the database but not in the app. For a FORM the publish is not optional in another " +
                 "sense too: a PATCH of systemform.formxml writes the unpublished form while a GET " +
                 "returns the published one, so the change stays invisible even to a read-back until " +
                 "you publish the table. Table and column metadata is different — it is eventually " +
                 "consistent and catches up on its own within seconds, though clients still need the " +
                 "publish. NOTE: PublishXml cannot publish code components (PCF); those travel by " +
                 "solution import or `pac pcf push`. Prefer a targeted publish: PublishAllXml can " +
                 "run for minutes on a large org.")]
    public static async Task<string> PublishCustomizations(
        PublishService svc,
        ConfigProvider config,
        [Description("Comma-separated table logical names to publish, e.g. 'sample_purchaseorder,account'. Publishing a table also publishes its forms, views and ribbons.")] string? entities = null,
        [Description("Comma-separated web resource names or GUIDs to publish")] string? webResources = null,
        [Description("true to run PublishAllXml instead (slow — publishes the whole org)")] bool all = false,
        [Description("Comma-separated dashboard GUIDs (SystemForm ids). Ids only, names are not accepted here.")] string? dashboards = null,
        [Description("Comma-separated unique names of global choices (option sets)")] string? optionSets = null,
        [Description("true to publish the site map (a singleton — no value needed)")] bool siteMap = false,
        [Description("true to publish the application ribbon (a singleton — no value needed)")] bool ribbons = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var entityList = Split(entities);
            var webResourceList = Split(webResources);
            var dashboardList = Split(dashboards);
            var optionSetList = Split(optionSets);

            var parameterXml = await svc.PublishAsync(
                env.OrgUrl, entityList, webResourceList, all,
                dashboardList, optionSetList, siteMap, ribbons, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                all,
                entities = entityList,
                webResources = webResourceList,
                dashboards = dashboardList,
                optionSets = optionSetList,
                siteMap,
                ribbons,
                parameterXml
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
