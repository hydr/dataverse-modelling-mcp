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
                 "ParameterXml assembled from the given tables and web resources, or PublishAllXml " +
                 "when all=true. Needed after nearly every Web API change to forms, views, ribbons, " +
                 "modern commands, web resources or the site map — before publishing, the change " +
                 "exists in the database but not in the app. Prefer a targeted publish: " +
                 "PublishAllXml can run for minutes on a large org.")]
    public static async Task<string> PublishCustomizations(
        PublishService svc,
        ConfigProvider config,
        [Description("Comma-separated table logical names to publish, e.g. 'sample_purchaseorder,account'")] string? entities = null,
        [Description("Comma-separated web resource names or GUIDs to publish")] string? webResources = null,
        [Description("true to run PublishAllXml instead (slow — publishes the whole org)")] bool all = false,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var entityList = Split(entities);
            var webResourceList = Split(webResources);

            var parameterXml = await svc.PublishAsync(env.OrgUrl, entityList, webResourceList, all, ct);

            return JsonSerializer.Serialize(new
            {
                success = true,
                all,
                entities = entityList,
                webResources = webResourceList,
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
