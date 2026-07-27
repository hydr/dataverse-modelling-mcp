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

    [McpServerTool(Name = "view_create")]
    [Description("Create a new saved view for a Dataverse table from FetchXml + LayoutXml. " +
                 "Optionally adds the new view to a solution (component type 26 = SavedQuery).")]
    public static async Task<string> ViewCreate(
        ViewService svc,
        SolutionService solutionSvc,
        ConfigProvider config,
        [Description("Logical name of the table the view belongs to (e.g. 'sample_purchaseorder')")] string tableLogicalName,
        [Description("Display name of the new view")] string name,
        [Description("FetchXml defining the view's query")] string fetchXml,
        [Description("LayoutXml defining the view's grid columns")] string layoutXml,
        [Description("Optional description")] string? description = null,
        [Description("Query type code: 0=Public (default), 1=AdvancedFind, 2=Associated, 4=QuickFind")] int queryType = 0,
        [Description("true to make this the default view of the table")] bool isDefault = false,
        [Description("Optional solution unique name — the new view is added to it as a component")] string? solutionUniqueName = null,
        CancellationToken ct = default)
    {
        try
        {
            var env = config.GetActiveEnvironment();
            var viewId = await svc.CreateAsync(
                env.OrgUrl, tableLogicalName, name, fetchXml, layoutXml, description, queryType, isDefault, ct);

            var addedToSolution = false;
            if (!string.IsNullOrWhiteSpace(solutionUniqueName))
            {
                if (viewId == Guid.Empty)
                    throw new InvalidOperationException(
                        "View was created but Dataverse did not return its id — cannot add it to the solution.");

                // 26 = SavedQuery
                await solutionSvc.AddComponentAsync(env.OrgUrl, solutionUniqueName, viewId, 26, ct);
                addedToSolution = true;
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                viewId,
                tableLogicalName,
                name,
                queryType,
                isDefault,
                solutionUniqueName,
                addedToSolution
            }, JsonOptions);
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
