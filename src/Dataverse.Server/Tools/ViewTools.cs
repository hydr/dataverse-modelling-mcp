namespace Dataverse.Server.Tools;

using System.ComponentModel;
using System.Text.Json;
using Dataverse.Core.Config;
using Dataverse.Core.Services;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ViewTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "view_list")]
    [Description("List saved views for a Dataverse table.")]
    public static async Task<string> ViewList(
        ViewService svc,
        ConfigProvider config,
        [Description("Logical name of the table")] string tableLogicalName,
        [Description("Optional view type filter: 'public', 'advancedfind', 'associated', 'quickfind'")] string? viewType = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var result = await svc.ListAsync(env.OrgUrl, tableLogicalName, viewType, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "view_get")]
    [Description("Get the full definition of a saved view, including its FetchXml and LayoutXml.")]
    public static async Task<string> ViewGet(
        ViewService svc,
        ConfigProvider config,
        [Description("The view GUID")] string viewId,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(viewId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid viewId GUID format." });

            var env = config.GetActiveEnvironment();
            var result = await svc.GetAsync(env.OrgUrl, id, ct);
            return result is null
                ? JsonSerializer.Serialize(new { error = "View not found." })
                : JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "view_update")]
    [Description("Update properties of a saved view (e.g. name, fetchxml, layoutxml).")]
    public static async Task<string> ViewUpdate(
        ViewService svc,
        ConfigProvider config,
        [Description("The view GUID")] string viewId,
        [Description("JSON object with properties to update")] string propertiesJson,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(viewId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid viewId GUID format." });

            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesJson)
                        ?? throw new ArgumentException("propertiesJson could not be parsed.");
            var env = config.GetActiveEnvironment();
            await svc.UpdateAsync(env.OrgUrl, id, props, ct);
            return JsonSerializer.Serialize(new { success = true, viewId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "view_add_column")]
    [Description("Add a column to the layout of an existing saved view.")]
    public static async Task<string> ViewAddColumn(
        ViewService svc,
        ConfigProvider config,
        [Description("The view GUID")] string viewId,
        [Description("Logical name of the attribute to add")] string attributeLogicalName,
        [Description("Column width in pixels (default 100)")] int? width = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(viewId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid viewId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.AddColumnAsync(env.OrgUrl, id, attributeLogicalName, width, ct);
            return JsonSerializer.Serialize(new { success = true, viewId, attributeLogicalName });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }

    [McpServerTool(Name = "view_set_sort")]
    [Description("Set the sort order on a saved view's FetchXml.")]
    public static async Task<string> ViewSetSort(
        ViewService svc,
        ConfigProvider config,
        [Description("The view GUID")] string viewId,
        [Description("Logical name of the attribute to sort by")] string attributeLogicalName,
        [Description("true for descending order, false for ascending")] bool descending = false,
        CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(viewId, out var id))
                return JsonSerializer.Serialize(new { error = "Invalid viewId GUID format." });

            var env = config.GetActiveEnvironment();
            await svc.SetSortAsync(env.OrgUrl, id, attributeLogicalName, descending, ct);
            return JsonSerializer.Serialize(new { success = true, viewId, attributeLogicalName, descending });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, details = ex.GetType().Name });
        }
    }
}
